using Turno.Core.Domain;

namespace Turno.Core.Options;

public sealed class WorkerOptions
{
    public const string SectionName = "Turno:Worker";

    public string? Name { get; set; }
    public List<QueueOptions> Queues { get; set; } = [];
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConcurrencyKeyRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ProgressThrottle { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<QueueOptions> EffectiveQueues() =>
        Queues.Count == 0
            ? [new QueueOptions()]
            : Queues
                .GroupBy(q => QueueNames.Normalize(q.Name))
                .Select(g => new QueueOptions { Name = g.Key, Concurrency = g.Max(q => q.Concurrency), PrefetchCount = g.Max(q => q.PrefetchCount) })
                .ToArray();
}

public sealed class QueueOptions
{
    public string Name { get; set; } = QueueNames.Default;
    public int Concurrency { get; set; } = 4;
    public int? PrefetchCount { get; set; }
}

public sealed class SchedulerOptions
{
    public const string SectionName = "Turno:Scheduler";

    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);
    public int BatchSize { get; set; } = 200;
}

public sealed class RecoveryOptions
{
    public const string SectionName = "Turno:Recovery";

    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan LeaseGracePeriod { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan RedispatchEnqueuedAfter { get; set; } = TimeSpan.Zero;
    public int BatchSize { get; set; } = 100;
}

public sealed class RetentionOptions
{
    public const string SectionName = "Turno:Retention";

    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan SucceededJobs { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan CancelledJobs { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan FailedJobs { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan OfflineWorkers { get; set; } = TimeSpan.FromDays(1);
    public TimeSpan StaleWorkerAfter { get; set; } = TimeSpan.FromSeconds(30);
    public int BatchSize { get; set; } = 1000;
}

public sealed class ResilienceOptions
{
    public const string SectionName = "Turno:Resilience";

    public int ImmediateRetries { get; set; } = 2;
    public TimeSpan ImmediateRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);
    public int DefaultMaxAttempts { get; set; } = 5;
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromMinutes(15);
    public double BackoffJitter { get; set; } = 0.2;
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
    public Dictionary<string, JobTypeResilienceOptions> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int ImmediateRetriesFor(string type) =>
        Types.TryGetValue(type, out var custom) && custom.ImmediateRetries is { } value ? value : ImmediateRetries;

    public int MaxAttemptsFor(string type) =>
        Types.TryGetValue(type, out var custom) && custom.MaxAttempts is { } value ? value : DefaultMaxAttempts;

    public TimeSpan TimeoutFor(string type) =>
        Types.TryGetValue(type, out var custom) && custom.Timeout is { } value ? value : DefaultTimeout;

    public CircuitBreakerOptions CircuitBreakerFor(string type) =>
        Types.TryGetValue(type, out var custom) && custom.CircuitBreaker is { } value ? value : CircuitBreaker;
}

public sealed class JobTypeResilienceOptions
{
    public int? ImmediateRetries { get; set; }
    public int? MaxAttempts { get; set; }
    public TimeSpan? Timeout { get; set; }
    public CircuitBreakerOptions? CircuitBreaker { get; set; }
}

public sealed class CircuitBreakerOptions
{
    public bool Enabled { get; set; } = true;
    public double FailureRatio { get; set; } = 0.5;
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public int MinimumThroughput { get; set; } = 10;
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
