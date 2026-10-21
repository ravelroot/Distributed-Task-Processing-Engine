using System.Text.Json;
using Microsoft.Extensions.Logging;
using Turno.Contracts;

namespace Turno.Core.Handlers;

public interface IJobHandler
{
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

public abstract class JobHandler<TPayload> : IJobHandler
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = context.GetPayload<TPayload>()
            ?? throw new PermanentJobException($"A tarefa '{context.Type}' exige um payload.");
        return ExecuteAsync(payload, context, cancellationToken);
    }

    protected abstract Task ExecuteAsync(TPayload payload, JobContext context, CancellationToken cancellationToken);
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class JobTypeAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public interface IJobProgressSink
{
    ValueTask ReportAsync(int percent, CancellationToken cancellationToken);
}

public sealed class JobContext
{
    private readonly IJobProgressSink progressSink;

    public JobContext(
        Guid jobId,
        string type,
        string queue,
        int attempt,
        int maxAttempts,
        string? payload,
        string workerId,
        DateTimeOffset startedAt,
        IJobProgressSink progressSink,
        ILogger logger)
    {
        JobId = jobId;
        Type = type;
        Queue = queue;
        Attempt = attempt;
        MaxAttempts = maxAttempts;
        Payload = payload;
        WorkerId = workerId;
        StartedAt = startedAt;
        this.progressSink = progressSink;
        Logger = logger;
    }

    public Guid JobId { get; }
    public string Type { get; }
    public string Queue { get; }
    public int Attempt { get; }
    public int MaxAttempts { get; }
    public string? Payload { get; }
    public string WorkerId { get; }
    public DateTimeOffset StartedAt { get; }
    public ILogger Logger { get; }
    public object? Result { get; private set; }
    public bool IsLastAttempt => Attempt >= MaxAttempts;

    public T? GetPayload<T>()
    {
        if (string.IsNullOrWhiteSpace(Payload))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(Payload, TurnoJson.Options);
        }
        catch (JsonException ex)
        {
            throw new PermanentJobException($"Payload inválido para a tarefa '{Type}': {ex.Message}", ex);
        }
    }

    public void SetResult(object? value) => Result = value;

    public ValueTask ReportProgressAsync(int percent, CancellationToken cancellationToken = default) =>
        progressSink.ReportAsync(Math.Clamp(percent, 0, 100), cancellationToken);
}

public class PermanentJobException : Exception
{
    public PermanentJobException()
    {
    }

    public PermanentJobException(string message) : base(message)
    {
    }

    public PermanentJobException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
