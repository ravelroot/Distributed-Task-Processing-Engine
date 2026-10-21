using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;
using Turno.Core.Handlers;
using Turno.Core.Options;

namespace Turno.Infrastructure.Resilience;

public sealed partial class JobResiliencePipelines : IDisposable
{
    public static readonly ResiliencePropertyKey<TimeSpan> TimeoutKey = new("turno.timeout");

    private readonly ResiliencePipelineRegistry<string> registry = new();
    private readonly IOptionsMonitor<ResilienceOptions> options;
    private readonly ILogger<JobResiliencePipelines> logger;
    private readonly TimeProvider timeProvider;

    public JobResiliencePipelines(IOptionsMonitor<ResilienceOptions> options, ILogger<JobResiliencePipelines> logger, TimeProvider timeProvider)
    {
        this.options = options;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    public ResiliencePipeline Get(string jobType) =>
        registry.GetOrAddPipeline(jobType, (builder, context) => Configure(builder, context.PipelineKey));

    public TimeSpan TimeoutFor(string jobType, int? timeoutSeconds) =>
        timeoutSeconds is > 0 ? TimeSpan.FromSeconds(timeoutSeconds.Value) : options.CurrentValue.TimeoutFor(jobType);

    public TimeSpan BreakDurationFor(string jobType) =>
        Clamp(options.CurrentValue.CircuitBreakerFor(jobType).BreakDuration, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1));

    public static bool IsTransient(Exception exception) => exception is not (
        PermanentJobException or
        OperationCanceledException or
        BrokenCircuitException or
        TimeoutRejectedException);

    public void Dispose() => registry.Dispose();

    private void Configure(ResiliencePipelineBuilder builder, string jobType)
    {
        var settings = options.CurrentValue;
        builder.TimeProvider = timeProvider;
        var fallbackTimeout = Clamp(settings.TimeoutFor(jobType), TimeSpan.FromSeconds(1), TimeSpan.FromDays(1));

        builder.AddTimeout(new TimeoutStrategyOptions
        {
            TimeoutGenerator = args =>
            {
                var timeout = args.Context.Properties.GetValue(TimeoutKey, fallbackTimeout);
                return ValueTask.FromResult(Clamp(timeout, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1)));
            },
            OnTimeout = args =>
            {
                LogTimeout(logger, jobType, args.Timeout);
                return default;
            }
        });

        var retries = Math.Clamp(settings.ImmediateRetriesFor(jobType), 0, 20);
        if (retries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
                MaxRetryAttempts = retries,
                Delay = Clamp(settings.ImmediateRetryDelay, TimeSpan.Zero, TimeSpan.FromMinutes(5)),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    LogImmediateRetry(logger, jobType, args.AttemptNumber + 1, retries, args.RetryDelay, args.Outcome.Exception?.Message);
                    return default;
                }
            });
        }

        var breaker = settings.CircuitBreakerFor(jobType);
        if (breaker.Enabled)
        {
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
                FailureRatio = Math.Clamp(breaker.FailureRatio, 0.01, 1),
                SamplingDuration = Clamp(breaker.SamplingDuration, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1)),
                MinimumThroughput = Math.Max(2, breaker.MinimumThroughput),
                BreakDuration = Clamp(breaker.BreakDuration, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1)),
                OnOpened = args =>
                {
                    LogCircuitOpened(logger, jobType, args.BreakDuration, args.Outcome.Exception?.Message);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    LogCircuitHalfOpened(logger, jobType);
                    return default;
                },
                OnClosed = _ =>
                {
                    LogCircuitClosed(logger, jobType);
                    return default;
                }
            });
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tempo limite de {Timeout} atingido em uma tarefa '{JobType}'.")]
    private static partial void LogTimeout(ILogger logger, string jobType, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tarefa '{JobType}' falhou; nova tentativa imediata {Attempt}/{Max} em {Delay}: {Error}")]
    private static partial void LogImmediateRetry(ILogger logger, string jobType, int attempt, int max, TimeSpan delay, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disjuntor ABERTO para '{JobType}' por {Duration}: {Error}")]
    private static partial void LogCircuitOpened(ILogger logger, string jobType, TimeSpan duration, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disjuntor MEIO-ABERTO para '{JobType}': testando a próxima execução.")]
    private static partial void LogCircuitHalfOpened(ILogger logger, string jobType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disjuntor FECHADO para '{JobType}'.")]
    private static partial void LogCircuitClosed(ILogger logger, string jobType);
}
