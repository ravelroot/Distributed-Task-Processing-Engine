using Microsoft.EntityFrameworkCore;
using Turno.Contracts;
using Turno.Core.Domain;

namespace Turno.Infrastructure.Jobs;

public sealed record JobSubmission(
    string Type,
    string Queue,
    string? Payload,
    DateTimeOffset? RunAt,
    int MaxAttempts,
    int? TimeoutSeconds,
    string? IdempotencyKey,
    string? ConcurrencyKey,
    Guid? RecurringJobId,
    string Reason);

public sealed record SubmissionResult(Guid Id, JobState State, bool Duplicate);

public sealed partial class JobStore
{
    public async Task<SubmissionResult> SubmitAsync(JobSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.IdempotencyKey is { } key && await FindByIdempotencyKeyAsync(key, cancellationToken) is { } existing)
        {
            return existing;
        }

        var job = await StageAsync(submission, clock.GetUtcNow(), cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (submission.IdempotencyKey is not null && IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return await FindByIdempotencyKeyAsync(submission.IdempotencyKey, cancellationToken) ?? throw new InvalidOperationException("Conflito de idempotência sem tarefa correspondente.", ex);
        }

        return new SubmissionResult(job.Id, job.State, false);
    }

    private async Task<Job> StageAsync(JobSubmission submission, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = new Job
        {
            Id = Guid.CreateVersion7(now),
            Type = submission.Type,
            Queue = submission.Queue,
            Payload = submission.Payload,
            MaxAttempts = submission.MaxAttempts,
            TimeoutSeconds = submission.TimeoutSeconds,
            IdempotencyKey = submission.IdempotencyKey,
            ConcurrencyKey = submission.ConcurrencyKey,
            RecurringJobId = submission.RecurringJobId,
            CreatedAt = now
        };

        string reason;
        if (submission.RunAt is { } runAt && runAt > now.AddSeconds(1))
        {
            job.State = JobState.Scheduled;
            job.ScheduledAt = runAt.ToUniversalTime();
            reason = $"{submission.Reason} Agendada para {job.ScheduledAt:dd/MM/yyyy HH:mm:ss} UTC.";
        }
        else
        {
            job.State = JobState.Enqueued;
            job.DispatchId = Guid.NewGuid();
            job.EnqueuedAt = now;
            reason = submission.Reason;
            await DispatchAsync(job.Id, job.DispatchId.Value, job.Type, job.Queue, job.ConcurrencyKey, cancellationToken);
        }

        db.Jobs.Add(job);
        AddHistory(job.Id, null, job.State, reason, null, now);
        await PublishStatusAsync(Snapshot(job), job.State, reason, now, cancellationToken);
        return job;
    }

    private Task<SubmissionResult?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.IdempotencyKey == key)
            .Select(j => new SubmissionResult(j.Id, j.State, true))
            .FirstOrDefaultAsync(cancellationToken);

    private static JobSnapshot Snapshot(Job job) =>
        new(job.Id, job.Type, job.Queue, job.State, job.Attempt, job.MaxAttempts, job.DispatchId, job.WorkerId, job.ConcurrencyKey, job.Payload, job.TimeoutSeconds, job.LeaseExpiresAt);
}
