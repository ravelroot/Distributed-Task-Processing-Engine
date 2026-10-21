using System.Text.Json;

namespace Turno.Contracts.Api;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record EnqueueJobRequest
{
    public required string Type { get; init; }
    public string? Queue { get; init; }
    public JsonElement? Payload { get; init; }
    public DateTimeOffset? RunAt { get; init; }
    public int? DelaySeconds { get; init; }
    public int? MaxAttempts { get; init; }
    public int? TimeoutSeconds { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? ConcurrencyKey { get; init; }
}

public sealed record EnqueueJobResponse(Guid Id, JobState State, bool Duplicate);

public sealed record JobSummary(
    Guid Id,
    string Type,
    string Queue,
    JobState State,
    int Attempt,
    int MaxAttempts,
    int? Progress,
    string? WorkerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? LastError,
    bool CancellationRequested);

public sealed record JobAttemptDto(
    int Number,
    string? WorkerId,
    AttemptOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    string? ErrorType,
    string? ErrorMessage);

public sealed record JobStateChangeDto(
    JobState? From,
    JobState To,
    string? Reason,
    string? WorkerId,
    DateTimeOffset OccurredAt);

public sealed record JobDetails(
    JobSummary Job,
    string? Payload,
    string? Result,
    string? IdempotencyKey,
    string? ConcurrencyKey,
    int? TimeoutSeconds,
    Guid? RecurringJobId,
    string? RecurringJobName,
    DateTimeOffset? EnqueuedAt,
    DateTimeOffset? LeaseExpiresAt,
    IReadOnlyList<JobAttemptDto> Attempts,
    IReadOnlyList<JobStateChangeDto> History);

public sealed record ActionResponse(Guid? Id, JobState? State, string Message);

public sealed record EnqueueBatchRequest(IReadOnlyList<EnqueueJobRequest> Jobs);

public sealed record EnqueueBatchResponse(IReadOnlyList<EnqueueJobResponse> Jobs, int Created, int Duplicates);
