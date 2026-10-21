using System.Text.Json;

namespace Turno.Contracts.Api;

public sealed record StateCounts(long Scheduled, long Enqueued, long Processing, long Succeeded, long Failed, long Cancelled)
{
    public long Total => Scheduled + Enqueued + Processing + Succeeded + Failed + Cancelled;

    public long Get(JobState state) => state switch
    {
        JobState.Scheduled => Scheduled,
        JobState.Enqueued => Enqueued,
        JobState.Processing => Processing,
        JobState.Succeeded => Succeeded,
        JobState.Failed => Failed,
        JobState.Cancelled => Cancelled,
        _ => 0
    };
}

public sealed record ThroughputPoint(DateTimeOffset Minute, int Succeeded, int Failed);

public sealed record QueueStats(string Queue, long Scheduled, long Enqueued, long Processing);

public sealed record StatsResponse(
    StateCounts Counts,
    long SucceededLast24Hours,
    long FailedAttemptsLast24Hours,
    double? AverageDurationMs,
    int WorkersOnline,
    int CapacityOnline,
    long PendingDeadLetters,
    IReadOnlyList<ThroughputPoint> Throughput,
    IReadOnlyList<QueueStats> Queues,
    DateTimeOffset GeneratedAt);

public sealed record WorkerDto(
    string Id,
    string HostName,
    int ProcessId,
    WorkerStatus Status,
    IReadOnlyList<string> Queues,
    IReadOnlyList<string> JobTypes,
    int Concurrency,
    int RunningJobs,
    long ProcessedJobs,
    long FailedJobs,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeatAt,
    string Version);

public sealed record RecurringJobDto(
    Guid Id,
    string Name,
    string Type,
    string Queue,
    string Cron,
    string TimeZone,
    string? Payload,
    bool Enabled,
    bool AllowConcurrent,
    int MaxAttempts,
    int? TimeoutSeconds,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    Guid? LastJobId,
    JobState? LastJobState,
    DateTimeOffset UpdatedAt);

public sealed record UpsertRecurringJobRequest
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Cron { get; init; }
    public string? Queue { get; init; }
    public string? TimeZone { get; init; }
    public JsonElement? Payload { get; init; }
    public bool Enabled { get; init; } = true;
    public bool AllowConcurrent { get; init; }
    public int? MaxAttempts { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed record DeadLetterDto(
    Guid Id,
    Guid JobId,
    string Type,
    string Queue,
    int Attempts,
    string? ErrorType,
    string? ErrorMessage,
    string? StackTrace,
    string? WorkerId,
    DateTimeOffset FailedAt,
    DateTimeOffset? RequeuedAt);

public sealed record ServerInfo(string Version, bool ApiKeyRequired, string Transport, string Environment);
