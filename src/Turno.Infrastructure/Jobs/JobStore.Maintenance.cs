using Microsoft.EntityFrameworkCore;
using Turno.Contracts;
using Turno.Core.Domain;
using Turno.Core.Options;
using Turno.Core.Scheduling;

namespace Turno.Infrastructure.Jobs;

public sealed record RetentionResult(int SucceededJobs, int CancelledJobs, int FailedJobs, int OfflineWorkers);

public sealed partial class JobStore
{
    public async Task<int> EnqueueDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Scheduled && j.ScheduledAt <= now)
            .OrderBy(j => j.ScheduledAt)
            .Take(Math.Max(1, batchSize))
            .Select(j => new JobSnapshot(j.Id, j.Type, j.Queue, j.State, j.Attempt, j.MaxAttempts, j.DispatchId, j.WorkerId, j.ConcurrencyKey, null, j.TimeoutSeconds, j.LeaseExpiresAt))
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var enqueued = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var job in due)
        {
            var dispatchId = Guid.NewGuid();
            var updated = await db.Jobs
                .Where(j => j.Id == job.Id && j.State == JobState.Scheduled && j.ScheduledAt <= now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, JobState.Enqueued)
                    .SetProperty(j => j.DispatchId, dispatchId)
                    .SetProperty(j => j.EnqueuedAt, now), cancellationToken);

            if (updated == 0)
            {
                continue;
            }

            enqueued++;
            await DispatchAsync(job.Id, dispatchId, job.Type, job.Queue, job.ConcurrencyKey, cancellationToken);
            AddHistory(job.Id, JobState.Scheduled, JobState.Enqueued, "Horário atingido; enviada para a fila.", null, now);
            await PublishStatusAsync(job, JobState.Enqueued, null, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return enqueued;
    }

    public async Task<int> RecoverExpiredLeasesAsync(TimeSpan gracePeriod, int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var threshold = now - gracePeriod;
        var expired = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Processing && j.LeaseExpiresAt < threshold)
            .OrderBy(j => j.LeaseExpiresAt)
            .Take(Math.Max(1, batchSize))
            .Select(j => new JobSnapshot(j.Id, j.Type, j.Queue, j.State, j.Attempt, j.MaxAttempts, j.DispatchId, j.WorkerId, j.ConcurrencyKey, j.Payload, j.TimeoutSeconds, j.LeaseExpiresAt))
            .ToListAsync(cancellationToken);

        var recovered = 0;
        foreach (var job in expired)
        {
            if (await RecoverAsync(job, now, cancellationToken))
            {
                recovered++;
            }
        }

        return recovered;
    }

    private async Task<bool> RecoverAsync(JobSnapshot job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var previousDispatch = job.DispatchId;
        var previousLease = job.LeaseExpiresAt;
        var exhausted = job.Attempt >= job.MaxAttempts;
        var lostWorker = job.WorkerId ?? "desconhecido";

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var query = db.Jobs.Where(j => j.Id == job.Id && j.State == JobState.Processing && j.DispatchId == previousDispatch && j.LeaseExpiresAt == previousLease);

        int updated;
        Guid dispatchId = Guid.NewGuid();
        if (exhausted)
        {
            updated = await query.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Failed)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.DispatchId, (Guid?)null)
                .SetProperty(j => j.LastError, "WorkerLost: o worker parou de responder durante a execução."), cancellationToken);
        }
        else
        {
            updated = await query.ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Enqueued)
                .SetProperty(j => j.DispatchId, dispatchId)
                .SetProperty(j => j.EnqueuedAt, now)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.Progress, (int?)null)
                .SetProperty(j => j.LastError, "WorkerLost: o worker parou de responder durante a execução."), cancellationToken);
        }

        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var attemptNumber = job.Attempt;
        var jobId = job.Id;
        await db.JobAttempts
            .Where(a => a.JobId == jobId && a.Number == attemptNumber && a.Outcome == AttemptOutcome.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Outcome, AttemptOutcome.Lost)
                .SetProperty(a => a.FinishedAt, now)
                .SetProperty(a => a.ErrorType, "WorkerLost")
                .SetProperty(a => a.ErrorMessage, $"O worker {lostWorker} parou de renovar o lease."), cancellationToken);

        if (exhausted)
        {
            var reason = $"O worker {lostWorker} parou de responder na tentativa {job.Attempt} de {job.MaxAttempts}. Tentativas esgotadas; enviada para a fila de erros.";
            AddHistory(job.Id, JobState.Processing, JobState.Failed, reason, null, now);
            await AddDeadLetterAsync(job, job.WorkerId, "WorkerLost", "O worker parou de responder durante a execução.", null, now, cancellationToken);
            await PublishStatusAsync(job, JobState.Failed, reason, now, cancellationToken);
        }
        else
        {
            var reason = $"O worker {lostWorker} parou de responder na tentativa {job.Attempt} de {job.MaxAttempts}. Tarefa devolvida à fila.";
            AddHistory(job.Id, JobState.Processing, JobState.Enqueued, reason, null, now);
            await DispatchAsync(job.Id, dispatchId, job.Type, job.Queue, job.ConcurrencyKey, cancellationToken);
            await PublishStatusAsync(job, JobState.Enqueued, reason, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> RedispatchStaleEnqueuedAsync(TimeSpan after, int batchSize, CancellationToken cancellationToken)
    {
        if (after <= TimeSpan.Zero)
        {
            return 0;
        }

        var now = clock.GetUtcNow();
        var threshold = now - after;
        var stale = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Enqueued && j.EnqueuedAt < threshold)
            .OrderBy(j => j.EnqueuedAt)
            .Take(Math.Max(1, batchSize))
            .Select(j => new JobSnapshot(j.Id, j.Type, j.Queue, j.State, j.Attempt, j.MaxAttempts, j.DispatchId, j.WorkerId, j.ConcurrencyKey, null, j.TimeoutSeconds, j.LeaseExpiresAt))
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        var count = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var job in stale)
        {
            var previous = job.DispatchId;
            var dispatchId = Guid.NewGuid();
            var updated = await db.Jobs
                .Where(j => j.Id == job.Id && j.State == JobState.Enqueued && j.DispatchId == previous)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.DispatchId, dispatchId)
                    .SetProperty(j => j.EnqueuedAt, now), cancellationToken);
            if (updated == 0)
            {
                continue;
            }

            count++;
            await DispatchAsync(job.Id, dispatchId, job.Type, job.Queue, job.ConcurrencyKey, cancellationToken);
            AddHistory(job.Id, JobState.Enqueued, JobState.Enqueued, $"Sem consumo há mais de {FormatDelay(after)}; mensagem reenviada.", null, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<int> TriggerDueRecurringAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await db.RecurringJobs
            .Where(r => r.Enabled && (r.NextRunAt == null || r.NextRunAt <= now))
            .OrderBy(r => r.NextRunAt)
            .Take(Math.Max(1, batchSize))
            .ToListAsync(cancellationToken);

        var triggered = 0;
        foreach (var recurring in due)
        {
            if (!CronSchedule.TryParse(recurring.Cron, recurring.TimeZone, out var schedule, out _))
            {
                recurring.Enabled = false;
                recurring.UpdatedAt = now;
                continue;
            }

            var next = schedule.GetNextOccurrence(now);
            if (recurring.NextRunAt is null)
            {
                recurring.NextRunAt = next;
                recurring.UpdatedAt = now;
                continue;
            }

            if (!recurring.AllowConcurrent && recurring.LastJobId is { } lastJobId &&
                await db.Jobs.AnyAsync(j => j.Id == lastJobId && (j.State == JobState.Scheduled || j.State == JobState.Enqueued || j.State == JobState.Processing), cancellationToken))
            {
                recurring.NextRunAt = next;
                recurring.UpdatedAt = now;
                continue;
            }

            var job = await StageAsync(FromRecurring(recurring, $"Disparada pelo agendamento '{recurring.Name}'."), now, cancellationToken);
            recurring.LastRunAt = now;
            recurring.LastJobId = job.Id;
            recurring.NextRunAt = next;
            recurring.UpdatedAt = now;
            triggered++;
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return triggered;
    }

    public async Task<RetentionResult> ApplyRetentionAsync(RetentionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var now = clock.GetUtcNow();
        var batch = Math.Max(1, options.BatchSize);
        var succeeded = await DeleteFinishedAsync(JobState.Succeeded, now - options.SucceededJobs, batch, cancellationToken);
        var cancelled = await DeleteFinishedAsync(JobState.Cancelled, now - options.CancelledJobs, batch, cancellationToken);
        var failed = await DeleteFinishedAsync(JobState.Failed, now - options.FailedJobs, batch, cancellationToken);

        var offlineThreshold = now - options.OfflineWorkers;
        var workers = await db.Workers
            .Where(w => w.Status == WorkerStatus.Offline && w.LastHeartbeatAt < offlineThreshold)
            .ExecuteDeleteAsync(cancellationToken);

        return new RetentionResult(succeeded, cancelled, failed, workers);
    }

    private async Task<int> DeleteFinishedAsync(JobState state, DateTimeOffset before, int batch, CancellationToken cancellationToken)
    {
        var ids = await db.Jobs.AsNoTracking()
            .Where(j => j.State == state && j.FinishedAt < before)
            .OrderBy(j => j.FinishedAt)
            .Take(batch)
            .Select(j => j.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return 0;
        }

        return await db.Jobs.Where(j => ids.Contains(j.Id) && j.State == state).ExecuteDeleteAsync(cancellationToken);
    }

    private JobSubmission FromRecurring(RecurringJob recurring, string reason) =>
        new(
            recurring.Type,
            recurring.Queue,
            recurring.Payload,
            null,
            recurring.MaxAttempts > 0 ? recurring.MaxAttempts : resilience.Value.MaxAttemptsFor(recurring.Type),
            recurring.TimeoutSeconds,
            null,
            null,
            recurring.Id,
            reason);
}
