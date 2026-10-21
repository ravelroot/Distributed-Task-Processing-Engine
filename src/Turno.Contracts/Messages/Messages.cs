namespace Turno.Contracts.Messages;

public sealed record ExecuteJob(Guid JobId, Guid DispatchId, string Type, string Queue, string? ConcurrencyKey);

public sealed record JobStatusChanged(
    Guid JobId,
    string Type,
    string Queue,
    JobState State,
    int Attempt,
    int MaxAttempts,
    string? WorkerId,
    int? Progress,
    string? Message,
    DateTimeOffset OccurredAt);

public sealed record JobCancellationRequested(Guid JobId, DateTimeOffset RequestedAt);

public sealed record JobDeadLettered(
    Guid DeadLetterId,
    Guid JobId,
    string Type,
    string Queue,
    string? Payload,
    int Attempts,
    string? ErrorType,
    string? ErrorMessage,
    string? WorkerId,
    DateTimeOffset FailedAt);

public sealed record WorkerStatusChanged(
    string WorkerId,
    string HostName,
    WorkerStatus Status,
    IReadOnlyList<string> Queues,
    int Concurrency,
    int RunningJobs,
    long ProcessedJobs,
    long FailedJobs,
    DateTimeOffset LastHeartbeatAt);
