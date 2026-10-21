using Turno.Contracts;

namespace Turno.Core.Domain;

public sealed class Job
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Queue { get; set; } = QueueNames.Default;
    public string? Payload { get; set; }
    public string? Result { get; set; }
    public JobState State { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int? Progress { get; set; }
    public Guid? DispatchId { get; set; }
    public string? WorkerId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public bool CancellationRequested { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? ConcurrencyKey { get; set; }
    public string? LastError { get; set; }
    public Guid? RecurringJobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? EnqueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public RecurringJob? RecurringJob { get; set; }
    public WorkerNode? Worker { get; set; }
    public List<JobAttempt> Attempts { get; set; } = [];
    public List<JobStateChange> History { get; set; } = [];
    public List<DeadLetter> DeadLetters { get; set; } = [];
}

public sealed class JobAttempt
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public int Number { get; set; }
    public string? WorkerId { get; set; }
    public AttemptOutcome Outcome { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }

    public Job? Job { get; set; }
}

public sealed class JobStateChange
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public JobState? FromState { get; set; }
    public JobState ToState { get; set; }
    public string? Reason { get; set; }
    public string? WorkerId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public Job? Job { get; set; }
}

public sealed class RecurringJob
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Queue { get; set; } = QueueNames.Default;
    public string Cron { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public string? Payload { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AllowConcurrent { get; set; }
    public int MaxAttempts { get; set; }
    public int? TimeoutSeconds { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastJobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }

    public List<Job> Jobs { get; set; } = [];
}

public sealed class WorkerNode
{
    public string Id { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public WorkerStatus Status { get; set; }
    public string[] Queues { get; set; } = [];
    public string[] JobTypes { get; set; } = [];
    public int Concurrency { get; set; }
    public int RunningJobs { get; set; }
    public long ProcessedJobs { get; set; }
    public long FailedJobs { get; set; }
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; }

    public List<Job> Jobs { get; set; } = [];
}

public sealed class DeadLetter
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Queue { get; set; } = QueueNames.Default;
    public string? Payload { get; set; }
    public int Attempts { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    public string? WorkerId { get; set; }
    public DateTimeOffset FailedAt { get; set; }
    public DateTimeOffset? RequeuedAt { get; set; }

    public Job? Job { get; set; }
}
