using Microsoft.EntityFrameworkCore;
using Turno.Contracts;
using Turno.Contracts.Messages;
using Turno.Core.Domain;
using Turno.Core.Scheduling;
using Turno.Infrastructure.Messaging;

namespace Turno.Infrastructure.Jobs;

public sealed record StartedJob(JobSnapshot Job, long AttemptId, DateTimeOffset StartedAt, DateTimeOffset LeaseExpiresAt);

public enum LeaseRenewal
{
    Renewed = 0,
    CancellationRequested = 1,
    Lost = 2
}

public sealed record FailureResult(bool Applied, bool DeadLettered, DateTimeOffset? RetryAt);

public sealed partial class JobStore
{
    public async Task<StartedJob?> TryStartAsync(Guid jobId, Guid dispatchId, string workerId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var lease = now + worker.Value.LeaseDuration;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.Jobs
            .Where(j => j.Id == jobId && j.DispatchId == dispatchId && j.State == JobState.Enqueued && !j.CancellationRequested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Processing)
                .SetProperty(j => j.Attempt, j => j.Attempt + 1)
                .SetProperty(j => j.WorkerId, workerId)
                .SetProperty(j => j.StartedAt, now)
                .SetProperty(j => j.LeaseExpiresAt, lease)
                .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.Progress, (int?)null), cancellationToken);

        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var job = await FindSnapshotAsync(jobId, cancellationToken) ?? throw new InvalidOperationException($"Tarefa {jobId} sumiu durante o início.");
        var attempt = new JobAttempt
        {
            JobId = jobId,
            Number = job.Attempt,
            WorkerId = workerId,
            Outcome = AttemptOutcome.Running,
            StartedAt = now
        };
        db.JobAttempts.Add(attempt);
        AddHistory(jobId, JobState.Enqueued, JobState.Processing, $"Tentativa {job.Attempt} de {job.MaxAttempts} iniciada.", workerId, now);
        await PublishStatusAsync(job, JobState.Processing, null, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StartedJob(job, attempt.Id, now, lease);
    }

    public async Task<LeaseRenewal> RenewLeaseAsync(StartedJob started, string workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(started);
        var until = clock.GetUtcNow() + worker.Value.LeaseDuration;
        var updated = await Owned(started, workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, until), cancellationToken);

        if (updated == 0)
        {
            return LeaseRenewal.Lost;
        }

        var jobId = started.Job.Id;
        var cancel = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.CancellationRequested)
            .FirstOrDefaultAsync(cancellationToken);
        return cancel ? LeaseRenewal.CancellationRequested : LeaseRenewal.Renewed;
    }

    public async Task<bool> ReportProgressAsync(StartedJob started, string workerId, int percent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(started);
        var value = Math.Clamp(percent, 0, 100);
        var updated = await Owned(started, workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Progress, value), cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        await PublishStatusAsync(started.Job, JobState.Processing, null, clock.GetUtcNow(), cancellationToken, workerId: workerId, progress: value);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteAsync(StartedJob started, string workerId, string? result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(started);
        var now = clock.GetUtcNow();
        var duration = Duration(started, now);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await Owned(started, workerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Succeeded)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.Progress, 100)
                .SetProperty(j => j.Result, result)
                .SetProperty(j => j.LastError, (string?)null)
                .SetProperty(j => j.DispatchId, (Guid?)null), cancellationToken);

        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await CloseAttemptAsync(started.AttemptId, AttemptOutcome.Succeeded, now, duration, null, null, cancellationToken);
        var message = $"Concluída em {FormatDelay(TimeSpan.FromMilliseconds(duration))}.";
        AddHistory(started.Job.Id, JobState.Processing, JobState.Succeeded, message, workerId, now);
        await PublishStatusAsync(started.Job, JobState.Succeeded, message, now, cancellationToken, workerId: workerId, progress: 100);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<FailureResult> FailAsync(StartedJob started, string workerId, Exception exception, bool permanent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(exception);
        var job = started.Job;
        var now = clock.GetUtcNow();
        var duration = Duration(started, now);
        var error = Describe(exception);
        var exhausted = permanent || job.Attempt >= job.MaxAttempts;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!exhausted)
        {
            var settings = resilience.Value;
            var delay = RetryBackoff.Compute(job.Attempt, settings.BackoffBase, settings.BackoffMax, settings.BackoffJitter);
            var retryAt = now + delay;
            var updated = await Owned(started, workerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, JobState.Scheduled)
                    .SetProperty(j => j.ScheduledAt, retryAt)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.Progress, (int?)null)
                    .SetProperty(j => j.LastError, error)
                    .SetProperty(j => j.DispatchId, (Guid?)null), cancellationToken);

            if (updated == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new FailureResult(false, false, null);
            }

            await CloseAttemptAsync(started.AttemptId, AttemptOutcome.Failed, now, duration, exception.GetType().FullName, exception.Message, cancellationToken);
            var reason = $"Falhou na tentativa {job.Attempt} de {job.MaxAttempts}. Nova tentativa em {FormatDelay(delay)}. {error}";
            AddHistory(job.Id, JobState.Processing, JobState.Scheduled, reason, workerId, now);
            await PublishStatusAsync(job, JobState.Scheduled, reason, now, cancellationToken, workerId: workerId);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FailureResult(true, false, retryAt);
        }

        var failed = await Owned(started, workerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Failed)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.LastError, error)
                .SetProperty(j => j.DispatchId, (Guid?)null), cancellationToken);

        if (failed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FailureResult(false, false, null);
        }

        await CloseAttemptAsync(started.AttemptId, AttemptOutcome.Failed, now, duration, exception.GetType().FullName, exception.Message, cancellationToken);
        var finalReason = permanent
            ? $"Erro permanente na tentativa {job.Attempt}; enviada para a fila de erros. {error}"
            : $"Tentativas esgotadas ({job.Attempt} de {job.MaxAttempts}); enviada para a fila de erros. {error}";
        AddHistory(job.Id, JobState.Processing, JobState.Failed, finalReason, workerId, now);
        await AddDeadLetterAsync(job, workerId, exception.GetType().FullName, exception.Message, exception.ToString(), now, cancellationToken);
        await PublishStatusAsync(job, JobState.Failed, finalReason, now, cancellationToken, workerId: workerId);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FailureResult(true, true, null);
    }

    public async Task<bool> DeferAsync(StartedJob started, string workerId, TimeSpan delay, AttemptOutcome outcome, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(started);
        var now = clock.GetUtcNow();
        var duration = Duration(started, now);
        var runAt = now + delay;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await Owned(started, workerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Scheduled)
                .SetProperty(j => j.ScheduledAt, runAt)
                .SetProperty(j => j.Attempt, j => j.Attempt - 1)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.Progress, (int?)null)
                .SetProperty(j => j.DispatchId, (Guid?)null), cancellationToken);

        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await CloseAttemptAsync(started.AttemptId, outcome, now, duration, null, reason, cancellationToken);
        var message = delay > TimeSpan.Zero ? $"{reason} Nova execução em {FormatDelay(delay)}, sem contar como tentativa." : $"{reason} Devolvida à fila, sem contar como tentativa.";
        AddHistory(started.Job.Id, JobState.Processing, JobState.Scheduled, message, workerId, now);
        await PublishStatusAsync(started.Job, JobState.Scheduled, message, now, cancellationToken, attempt: started.Job.Attempt - 1, workerId: workerId);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CancelRunningAsync(StartedJob started, string workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(started);
        var now = clock.GetUtcNow();
        var duration = Duration(started, now);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await Owned(started, workerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Cancelled)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.DispatchId, (Guid?)null), cancellationToken);

        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        const string message = "Cancelada a pedido durante a execução.";
        await CloseAttemptAsync(started.AttemptId, AttemptOutcome.Cancelled, now, duration, null, message, cancellationToken);
        AddHistory(started.Job.Id, JobState.Processing, JobState.Cancelled, message, workerId, now);
        await PublishStatusAsync(started.Job, JobState.Cancelled, message, now, cancellationToken, workerId: workerId);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeferBeforeStartAsync(ExecuteJob message, TimeSpan delay, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = clock.GetUtcNow();
        var runAt = now + delay;
        var updated = await db.Jobs
            .Where(j => j.Id == message.JobId && j.DispatchId == message.DispatchId && j.State == JobState.Enqueued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Scheduled)
                .SetProperty(j => j.ScheduledAt, runAt)
                .SetProperty(j => j.DispatchId, (Guid?)null), cancellationToken);

        if (updated == 0)
        {
            return false;
        }

        var lastReason = await db.JobStateChanges.AsNoTracking()
            .Where(h => h.JobId == message.JobId)
            .OrderByDescending(h => h.Id)
            .Select(h => h.Reason)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.Equals(lastReason, reason, StringComparison.Ordinal))
        {
            AddHistory(message.JobId, JobState.Enqueued, JobState.Scheduled, reason, null, now);
            if (await FindSnapshotAsync(message.JobId, cancellationToken) is { } job)
            {
                await PublishStatusAsync(job, JobState.Scheduled, reason, now, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private async Task AddDeadLetterAsync(JobSnapshot job, string? workerId, string? errorType, string? errorMessage, string? stackTrace, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deadLetter = new DeadLetter
        {
            Id = Guid.CreateVersion7(now),
            JobId = job.Id,
            Type = job.Type,
            Queue = job.Queue,
            Payload = job.Payload,
            Attempts = job.Attempt,
            ErrorType = Limits.Truncate(errorType, 300),
            ErrorMessage = Limits.Truncate(errorMessage, Limits.MaxErrorLength),
            StackTrace = Limits.Truncate(stackTrace, Limits.MaxStackTraceLength),
            WorkerId = workerId,
            FailedAt = now
        };
        db.DeadLetters.Add(deadLetter);

        var endpoint = await sendEndpoints.GetSendEndpoint(QueueAddress.DeadLetter(Prefix));
        await endpoint.Send(
            new JobDeadLettered(deadLetter.Id, job.Id, job.Type, job.Queue, job.Payload, job.Attempt, deadLetter.ErrorType, deadLetter.ErrorMessage, workerId, now),
            cancellationToken);
    }

    private Task<int> CloseAttemptAsync(long attemptId, AttemptOutcome outcome, DateTimeOffset now, long duration, string? errorType, string? errorMessage, CancellationToken cancellationToken)
    {
        var type = Limits.Truncate(errorType, 300);
        var message = Limits.Truncate(errorMessage, Limits.MaxErrorLength);
        return db.JobAttempts
            .Where(a => a.Id == attemptId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Outcome, outcome)
                .SetProperty(a => a.FinishedAt, now)
                .SetProperty(a => a.DurationMs, duration)
                .SetProperty(a => a.ErrorType, type)
                .SetProperty(a => a.ErrorMessage, message), cancellationToken);
    }

    private IQueryable<Job> Owned(StartedJob started, string workerId)
    {
        var jobId = started.Job.Id;
        var dispatchId = started.Job.DispatchId;
        return db.Jobs.Where(j => j.Id == jobId && j.DispatchId == dispatchId && j.WorkerId == workerId && j.State == JobState.Processing);
    }

    private static long Duration(StartedJob started, DateTimeOffset now) =>
        Math.Max(0, (long)(now - started.StartedAt).TotalMilliseconds);
}
