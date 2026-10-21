using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Turno.Contracts;
using Turno.Core.Domain;
using Turno.Core.Handlers;
using Turno.Core.Options;

namespace Turno.Worker;

public sealed partial class WorkerIdentity
{
    public WorkerIdentity(IOptions<WorkerOptions> options, JobHandlerRegistry handlers, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(clock);
        var settings = options.Value;
        HostName = Environment.MachineName;
        var name = Sanitize(string.IsNullOrWhiteSpace(settings.Name) ? HostName : settings.Name);
        Id = $"{name}-{Guid.NewGuid().ToString("N")[..8]}";
        ProcessId = Environment.ProcessId;
        StartedAt = clock.GetUtcNow();
        Queues = settings.EffectiveQueues().Select(q => q.Name).ToArray();
        Concurrency = settings.EffectiveQueues().Sum(q => Math.Max(1, q.Concurrency));
        JobTypes = handlers.Types.ToArray();
        Version = typeof(WorkerIdentity).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    public string Id { get; }
    public string HostName { get; }
    public int ProcessId { get; }
    public DateTimeOffset StartedAt { get; }
    public string[] Queues { get; }
    public string[] JobTypes { get; }
    public int Concurrency { get; }
    public string Version { get; }

    public WorkerNode ToNode(WorkerStatus status) => new()
    {
        Id = Id,
        HostName = HostName,
        ProcessId = ProcessId,
        Status = status,
        Queues = Queues,
        JobTypes = JobTypes,
        Concurrency = Concurrency,
        Version = Version,
        StartedAt = StartedAt
    };

    private static string Sanitize(string value)
    {
        var cleaned = InvalidCharacters().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        if (cleaned.Length == 0)
        {
            cleaned = "worker";
        }

        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidCharacters();
}
