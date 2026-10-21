using Microsoft.EntityFrameworkCore;
using Turno.Contracts;
using Turno.Contracts.Messages;
using Turno.Core.Domain;
using Turno.Core.Scheduling;

namespace Turno.Infrastructure.Jobs;

public enum CommandStatus
{
    Ok = 0,
    NotFound = 1,
    Conflict = 2,
    Invalid = 3
}

public sealed record CommandResult(CommandStatus Status, string Message, Guid? Id = null, JobState? State = null)
{
    public static CommandResult NotFound(string message) => new(CommandStatus.NotFound, message);

    public static CommandResult Conflict(string message, Guid? id = null, JobState? state = null) => new(CommandStatus.Conflict, message, id, state);

    public static CommandResult Invalid(string message) => new(CommandStatus.Invalid, message);
}

public sealed partial class JobStore
{
    public async Task<CommandResult> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await FindSnapshotAsync(jobId, cancellationToken);
        if (job is null)
        {
            return CommandResult.NotFound("Tarefa não encontrada.");
        }

        var now = clock.GetUtcNow();
        if (job.State is JobState.Scheduled or JobState.Enqueued)
        {
            var state = job.State;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var updated = await db.Jobs
                .Where(j => j.Id == jobId && j.State == state)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, JobState.Cancelled)
                    .SetProperty(j => j.FinishedAt, now)
                    .SetProperty(j => j.CancellationRequested, true)
                    .SetProperty(j => j.DispatchId, (Guid?)null), cancellationToken);

            if (updated == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return CommandResult.Conflict("A tarefa mudou de estado. Atualize e tente de novo.", jobId);
            }

            const string message = "Cancelada antes de iniciar.";
            AddHistory(jobId, state, JobState.Cancelled, message, null, now);
            await PublishStatusAsync(job, JobState.Cancelled, message, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CommandResult(CommandStatus.Ok, message, jobId, JobState.Cancelled);
        }

        if (job.State == JobState.Processing)
        {
            var updated = await db.Jobs
                .Where(j => j.Id == jobId && j.State == JobState.Processing)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.CancellationRequested, true), cancellationToken);
            if (updated == 0)
            {
                return CommandResult.Conflict("A tarefa terminou antes do cancelamento.", jobId);
            }

            await publisher.Publish(new JobCancellationRequested(jobId, now), cancellationToken);
            await PublishStatusAsync(job, JobState.Processing, "Cancelamento solicitado; o worker vai interromper a execução.", now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new CommandResult(CommandStatus.Ok, "Cancelamento solicitado. O worker interrompe a execução em instantes.", jobId, JobState.Processing);
        }

        return CommandResult.Conflict("A tarefa já foi finalizada.", jobId, job.State);
    }

    public async Task<CommandResult> RequeueAsync(Guid jobId, string reason, CancellationToken cancellationToken)
    {
        var job = await FindSnapshotAsync(jobId, cancellationToken);
        if (job is null)
        {
            return CommandResult.NotFound("Tarefa não encontrada.");
        }

        if (!JobStateMachine.CanRequeue(job.State))
        {
            return CommandResult.Conflict("Só tarefas com falha ou canceladas podem voltar para a fila.", jobId, job.State);
        }

        var now = clock.GetUtcNow();
        var dispatchId = Guid.NewGuid();
        var state = job.State;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.Jobs
            .Where(j => j.Id == jobId && j.State == state)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Enqueued)
                .SetProperty(j => j.Attempt, 0)
                .SetProperty(j => j.DispatchId, dispatchId)
                .SetProperty(j => j.EnqueuedAt, now)
                .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.StartedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.Progress, (int?)null)
                .SetProperty(j => j.CancellationRequested, false), cancellationToken);

        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CommandResult.Conflict("A tarefa mudou de estado. Atualize e tente de novo.", jobId);
        }

        await db.DeadLetters
            .Where(d => d.JobId == jobId && d.RequeuedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RequeuedAt, now), cancellationToken);

        await DispatchAsync(job.Id, dispatchId, job.Type, job.Queue, job.ConcurrencyKey, cancellationToken);
        AddHistory(jobId, state, JobState.Enqueued, reason, null, now);
        await PublishStatusAsync(job, JobState.Enqueued, reason, now, cancellationToken, attempt: 0);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CommandResult(CommandStatus.Ok, "Tarefa devolvida à fila.", jobId, JobState.Enqueued);
    }

    public async Task<CommandResult> DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await FindSnapshotAsync(jobId, cancellationToken);
        if (job is null)
        {
            return CommandResult.NotFound("Tarefa não encontrada.");
        }

        if (!JobStateMachine.CanDelete(job.State))
        {
            return CommandResult.Conflict("Cancele a tarefa antes de excluí-la.", jobId, job.State);
        }

        var state = job.State;
        var deleted = await db.Jobs.Where(j => j.Id == jobId && j.State == state).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0
            ? CommandResult.Conflict("A tarefa mudou de estado. Atualize e tente de novo.", jobId)
            : new CommandResult(CommandStatus.Ok, "Tarefa excluída.", jobId, state);
    }

    public async Task<CommandResult> RequeueDeadLetterAsync(Guid deadLetterId, CancellationToken cancellationToken)
    {
        var deadLetter = await db.DeadLetters.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deadLetterId, cancellationToken);
        if (deadLetter is null)
        {
            return CommandResult.NotFound("Registro não encontrado na fila de erros.");
        }

        return await RequeueAsync(deadLetter.JobId, "Reenfileirada a partir da fila de erros.", cancellationToken);
    }

    public async Task<CommandResult> DiscardDeadLetterAsync(Guid deadLetterId, CancellationToken cancellationToken)
    {
        var deleted = await db.DeadLetters.Where(d => d.Id == deadLetterId).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0
            ? CommandResult.NotFound("Registro não encontrado na fila de erros.")
            : new CommandResult(CommandStatus.Ok, "Registro descartado da fila de erros.", deadLetterId);
    }

    public async Task<CommandResult> UpsertRecurringAsync(RecurringDefinition definition, bool preserveEnabledState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!CronSchedule.TryParse(definition.Cron, definition.TimeZone, out var schedule, out var error))
        {
            return CommandResult.Invalid(error);
        }

        var now = clock.GetUtcNow();
        var recurring = await db.RecurringJobs.FirstOrDefaultAsync(r => r.Name == definition.Name, cancellationToken);
        var created = recurring is null;
        recurring ??= new RecurringJob { Id = Guid.CreateVersion7(now), Name = definition.Name, CreatedAt = now };

        var enabled = preserveEnabledState && !created ? recurring.Enabled : definition.Enabled;
        var scheduleChanged = created || recurring.Cron != schedule.Expression || recurring.TimeZone != definition.TimeZone || (!recurring.Enabled && enabled);
        recurring.Type = definition.Type;
        recurring.Queue = definition.Queue;
        recurring.Cron = schedule.Expression;
        recurring.TimeZone = definition.TimeZone;
        recurring.Payload = definition.Payload;
        recurring.Enabled = enabled;
        recurring.AllowConcurrent = definition.AllowConcurrent;
        recurring.MaxAttempts = definition.MaxAttempts;
        recurring.TimeoutSeconds = definition.TimeoutSeconds;
        recurring.UpdatedAt = now;
        if (scheduleChanged)
        {
            recurring.NextRunAt = schedule.GetNextOccurrence(now);
        }

        if (created)
        {
            db.RecurringJobs.Add(recurring);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new CommandResult(CommandStatus.Ok, created ? "Agendamento criado." : "Agendamento atualizado.", recurring.Id);
    }

    public async Task<CommandResult> SetRecurringEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        var recurring = await db.RecurringJobs.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recurring is null)
        {
            return CommandResult.NotFound("Agendamento não encontrado.");
        }

        var now = clock.GetUtcNow();
        recurring.Enabled = enabled;
        recurring.UpdatedAt = now;
        if (enabled && CronSchedule.TryParse(recurring.Cron, recurring.TimeZone, out var schedule, out _))
        {
            recurring.NextRunAt = schedule.GetNextOccurrence(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new CommandResult(CommandStatus.Ok, enabled ? "Agendamento ativado." : "Agendamento pausado.", id);
    }

    public async Task<CommandResult> TriggerRecurringNowAsync(Guid id, CancellationToken cancellationToken)
    {
        var recurring = await db.RecurringJobs.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recurring is null)
        {
            return CommandResult.NotFound("Agendamento não encontrado.");
        }

        var now = clock.GetUtcNow();
        var job = await StageAsync(FromRecurring(recurring, $"Disparada manualmente a partir do agendamento '{recurring.Name}'."), now, cancellationToken);
        recurring.LastRunAt = now;
        recurring.LastJobId = job.Id;
        recurring.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new CommandResult(CommandStatus.Ok, "Execução disparada.", job.Id, job.State);
    }

    public async Task<CommandResult> DeleteRecurringAsync(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await db.RecurringJobs.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0
            ? CommandResult.NotFound("Agendamento não encontrado.")
            : new CommandResult(CommandStatus.Ok, "Agendamento excluído.", id);
    }
}

public sealed record RecurringDefinition(
    string Name,
    string Type,
    string Queue,
    string Cron,
    string TimeZone,
    string? Payload,
    bool Enabled,
    bool AllowConcurrent,
    int MaxAttempts,
    int? TimeoutSeconds);
