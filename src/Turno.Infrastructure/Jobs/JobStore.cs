using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Turno.Contracts;
using Turno.Contracts.Messages;
using Turno.Core.Domain;
using Turno.Core.Options;
using Turno.Infrastructure.Messaging;
using Turno.Infrastructure.Options;
using Turno.Infrastructure.Persistence;

namespace Turno.Infrastructure.Jobs;

public sealed partial class JobStore(
    TurnoDbContext db,
    ISendEndpointProvider sendEndpoints,
    IPublishEndpoint publisher,
    TimeProvider clock,
    IOptions<MessagingOptions> messaging,
    IOptions<WorkerOptions> worker,
    IOptions<ResilienceOptions> resilience)
{
    private string Prefix => messaging.Value.QueuePrefix;

    private async Task DispatchAsync(Guid jobId, Guid dispatchId, string type, string queue, string? concurrencyKey, CancellationToken cancellationToken)
    {
        var endpoint = await sendEndpoints.GetSendEndpoint(QueueAddress.For(Prefix, queue));
        await endpoint.Send(new ExecuteJob(jobId, dispatchId, type, queue, concurrencyKey), cancellationToken);
    }

    private Task PublishStatusAsync(JobSnapshot job, JobState state, string? message, DateTimeOffset at, CancellationToken cancellationToken, int? attempt = null, string? workerId = null, int? progress = null) =>
        publisher.Publish(
            new JobStatusChanged(job.Id, job.Type, job.Queue, state, attempt ?? job.Attempt, job.MaxAttempts, workerId ?? job.WorkerId, progress, message, at),
            cancellationToken);

    private void AddHistory(Guid jobId, JobState? from, JobState to, string? reason, string? workerId, DateTimeOffset at) =>
        db.JobStateChanges.Add(new JobStateChange
        {
            JobId = jobId,
            FromState = from,
            ToState = to,
            Reason = Limits.Truncate(reason, 1000),
            WorkerId = workerId,
            OccurredAt = at
        });

    private Task<JobSnapshot?> FindSnapshotAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new JobSnapshot(j.Id, j.Type, j.Queue, j.State, j.Attempt, j.MaxAttempts, j.DispatchId, j.WorkerId, j.ConcurrencyKey, j.Payload, j.TimeoutSeconds, j.LeaseExpiresAt))
            .FirstOrDefaultAsync(cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static string Describe(Exception exception) =>
        Limits.Truncate($"{exception.GetType().Name}: {exception.Message}", Limits.MaxErrorLength)!;

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalSeconds < 1 ? "menos de 1 s"
        : delay.TotalMinutes < 1 ? $"{Math.Round(delay.TotalSeconds)} s"
        : delay.TotalHours < 1 ? $"{Math.Round(delay.TotalMinutes, 1)} min"
        : $"{Math.Round(delay.TotalHours, 1)} h";
}

public sealed record JobSnapshot(
    Guid Id,
    string Type,
    string Queue,
    JobState State,
    int Attempt,
    int MaxAttempts,
    Guid? DispatchId,
    string? WorkerId,
    string? ConcurrencyKey,
    string? Payload,
    int? TimeoutSeconds,
    DateTimeOffset? LeaseExpiresAt);
