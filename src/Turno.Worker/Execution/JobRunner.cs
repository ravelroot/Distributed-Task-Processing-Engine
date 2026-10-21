using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Turno.Contracts;
using Turno.Contracts.Messages;
using Turno.Core.Domain;
using Turno.Core.Handlers;
using Turno.Core.Locking;
using Turno.Core.Options;
using Turno.Infrastructure.Jobs;
using Turno.Infrastructure.Resilience;

namespace Turno.Worker.Execution;

public enum RunOutcome
{
    Skipped = 0,
    Succeeded = 1,
    Retrying = 2,
    DeadLettered = 3,
    Deferred = 4,
    Interrupted = 5,
    Cancelled = 6,
    Lost = 7
}

public sealed partial class JobRunner(
    IServiceScopeFactory scopeFactory,
    IDistributedLockProvider locks,
    JobHandlerRegistry handlers,
    JobResiliencePipelines pipelines,
    RunningJobs running,
    WorkerIdentity identity,
    IOptions<WorkerOptions> options,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<JobRunner> logger)
{
    private static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(20);

    public async Task<RunOutcome> RunAsync(ExecuteJob message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value;

        IDistributedLock? jobLock;
        try
        {
            jobLock = await locks.TryAcquireAsync(LockKeys.Job(message.JobId), settings.LockExpiry, cancellationToken);
        }
        catch (DistributedLockUnavailableException ex)
        {
            LogLockUnavailable(logger, message.JobId, ex.Message);
            await WithStoreAsync(store => store.DeferBeforeStartAsync(message, settings.ConcurrencyKeyRetryDelay, "Redis indisponível para o lock distribuído; nova tentativa em instantes.", cancellationToken));
            return RunOutcome.Deferred;
        }

        if (jobLock is null)
        {
            var deferred = await WithStoreAsync(store => store.DeferBeforeStartAsync(message, settings.ConcurrencyKeyRetryDelay, "Aguardando o lock da tarefa, ainda em posse de outro worker.", cancellationToken));
            LogDuplicate(logger, message.JobId, deferred);
            return deferred ? RunOutcome.Deferred : RunOutcome.Skipped;
        }

        await using (jobLock)
        {
            IDistributedLock? resourceLock = null;
            if (!string.IsNullOrWhiteSpace(message.ConcurrencyKey))
            {
                try
                {
                    resourceLock = await locks.TryAcquireAsync(LockKeys.Resource(message.ConcurrencyKey), settings.LockExpiry, cancellationToken);
                }
                catch (DistributedLockUnavailableException ex)
                {
                    LogLockUnavailable(logger, message.JobId, ex.Message);
                    await WithStoreAsync(store => store.DeferBeforeStartAsync(message, settings.ConcurrencyKeyRetryDelay, "Redis indisponível para o lock distribuído; nova tentativa em instantes.", cancellationToken));
                    return RunOutcome.Deferred;
                }

                if (resourceLock is null)
                {
                    var reason = $"Aguardando a chave de concorrência '{message.ConcurrencyKey}', em uso por outra tarefa.";
                    await WithStoreAsync(store => store.DeferBeforeStartAsync(message, settings.ConcurrencyKeyRetryDelay, reason, cancellationToken));
                    LogResourceBusy(logger, message.JobId, message.ConcurrencyKey);
                    return RunOutcome.Deferred;
                }
            }

            try
            {
                return await ExecuteLockedAsync(message, jobLock, resourceLock, cancellationToken);
            }
            finally
            {
                if (resourceLock is not null)
                {
                    await resourceLock.DisposeAsync();
                }
            }
        }
    }

    private async Task<RunOutcome> ExecuteLockedAsync(ExecuteJob message, IDistributedLock jobLock, IDistributedLock? resourceLock, CancellationToken cancellationToken)
    {
        var started = await WithStoreAsync(store => store.TryStartAsync(message.JobId, message.DispatchId, identity.Id, cancellationToken));
        if (started is null)
        {
            LogStale(logger, message.JobId, message.DispatchId);
            return RunOutcome.Skipped;
        }

        var job = started.Job;
        var settings = options.Value;
        using var tracked = running.Track(job.Id, job.Type);
        using var shutdown = lifetime.ApplicationStopping.Register(() => tracked.Stop(StopReason.Shutdown, DrainTime(settings)));
        using var consumerStop = cancellationToken.Register(() => tracked.Stop(StopReason.Shutdown, DrainTime(settings)));
        using var keeperStop = new CancellationTokenSource();
        var keeper = KeepLeaseAsync(started, tracked, jobLock, resourceLock, keeperStop.Token);

        var sink = new ProgressSink(this, started, settings.ProgressThrottle, clock);
        var context = new JobContext(job.Id, job.Type, job.Queue, job.Attempt, job.MaxAttempts, job.Payload, identity.Id, started.StartedAt, sink, logger);
        LogStarted(logger, job.Id, job.Type, job.Attempt, job.MaxAttempts);

        Exception? failure = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!handlers.TryGet(job.Type, out var handlerType))
            {
                throw new PermanentJobException($"Nenhum handler registrado para o tipo '{job.Type}' no worker {identity.Id}.");
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = (IJobHandler)scope.ServiceProvider.GetRequiredService(handlerType);
            var pipeline = pipelines.Get(job.Type);
            var resilienceContext = ResilienceContextPool.Shared.Get(tracked.Source.Token);
            resilienceContext.Properties.Set(JobResiliencePipelines.TimeoutKey, pipelines.TimeoutFor(job.Type, job.TimeoutSeconds));
            try
            {
                await pipeline.ExecuteAsync(
                    static async (ctx, state) => await state.Handler.ExecuteAsync(state.Context, ctx.CancellationToken),
                    resilienceContext,
                    (Handler: handler, Context: context));
            }
            finally
            {
                ResilienceContextPool.Shared.Return(resilienceContext);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            stopwatch.Stop();
            await keeperStop.CancelAsync();
            await keeper;
            running.Untrack(tracked);
        }

        return await FinishAsync(started, tracked, context, failure, stopwatch.Elapsed);
    }

    private async Task<RunOutcome> FinishAsync(StartedJob started, RunningJob tracked, JobContext context, Exception? failure, TimeSpan elapsed)
    {
        var job = started.Job;
        using var persist = new CancellationTokenSource(PersistTimeout);
        var token = persist.Token;
        var reason = tracked.Reason;

        if (failure is null)
        {
            if (reason is StopReason.LeaseLost or StopReason.LockLost)
            {
                LogOwnershipLost(logger, job.Id, reason.ToString());
                return RunOutcome.Lost;
            }

            var completed = await WithStoreAsync(store => store.CompleteAsync(started, identity.Id, SerializeResult(context.Result), token));
            running.RecordSuccess();
            LogSucceeded(logger, job.Id, job.Type, elapsed.TotalMilliseconds);
            return completed ? RunOutcome.Succeeded : RunOutcome.Lost;
        }

        if (failure is OperationCanceledException && reason != StopReason.None)
        {
            switch (reason)
            {
                case StopReason.CancellationRequested:
                    await WithStoreAsync(store => store.CancelRunningAsync(started, identity.Id, token));
                    LogCancelled(logger, job.Id);
                    return RunOutcome.Cancelled;

                case StopReason.Shutdown:
                    await WithStoreAsync(store => store.DeferAsync(started, identity.Id, TimeSpan.Zero, AttemptOutcome.Interrupted, "Execução interrompida porque o worker está sendo encerrado.", token));
                    LogInterrupted(logger, job.Id);
                    return RunOutcome.Interrupted;

                default:
                    LogOwnershipLost(logger, job.Id, reason.ToString());
                    return RunOutcome.Lost;
            }
        }

        if (failure is BrokenCircuitException)
        {
            var delay = pipelines.BreakDurationFor(job.Type);
            await WithStoreAsync(store => store.DeferAsync(started, identity.Id, delay, AttemptOutcome.Deferred, $"Disjuntor aberto para '{job.Type}' neste worker.", token));
            LogCircuitDeferred(logger, job.Id, job.Type, delay);
            return RunOutcome.Deferred;
        }

        var error = failure is TimeoutRejectedException timeout
            ? new JobTimeoutException($"A tarefa excedeu o tempo limite de {timeout.Timeout}.", timeout)
            : failure;
        var permanent = failure is PermanentJobException;
        var result = await WithStoreAsync(store => store.FailAsync(started, identity.Id, error, permanent, token));
        running.RecordFailure();

        if (!result.Applied)
        {
            LogOwnershipLost(logger, job.Id, "falha sem posse");
            return RunOutcome.Lost;
        }

        if (result.DeadLettered)
        {
            LogDeadLettered(logger, error, job.Id, job.Type, job.Attempt);
            return RunOutcome.DeadLettered;
        }

        LogRetrying(logger, job.Id, job.Type, job.Attempt, job.MaxAttempts, result.RetryAt, error.Message);
        return RunOutcome.Retrying;
    }

    private async Task KeepLeaseAsync(StartedJob started, RunningJob tracked, IDistributedLock jobLock, IDistributedLock? resourceLock, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var interval = settings.LeaseRenewalInterval > TimeSpan.Zero ? settings.LeaseRenewalInterval : TimeSpan.FromSeconds(10);
        var validUntil = started.LeaseExpiresAt;
        var warnedLock = false;
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (resourceLock is { IsHeld: false })
                {
                    tracked.Stop(StopReason.LockLost);
                    return;
                }

                if (!jobLock.IsHeld && !warnedLock)
                {
                    warnedLock = true;
                    LogJobLockLost(logger, started.Job.Id);
                }

                try
                {
                    var renewal = await WithStoreAsync(store => store.RenewLeaseAsync(started, identity.Id, cancellationToken));
                    switch (renewal)
                    {
                        case LeaseRenewal.Lost:
                            tracked.Stop(StopReason.LeaseLost);
                            return;
                        case LeaseRenewal.CancellationRequested:
                            tracked.Stop(StopReason.CancellationRequested);
                            return;
                        default:
                            validUntil = clock.GetUtcNow() + settings.LeaseDuration;
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRenewFailed(logger, started.Job.Id, ex.Message);
                    if (clock.GetUtcNow() + interval >= validUntil)
                    {
                        tracked.Stop(StopReason.LeaseLost);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task ReportProgressAsync(StartedJob started, int percent, CancellationToken cancellationToken)
    {
        try
        {
            await WithStoreAsync(store => store.ReportProgressAsync(started, identity.Id, percent, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProgressFailed(logger, started.Job.Id, ex.Message);
        }
    }

    private async Task<T> WithStoreAsync<T>(Func<JobStore, Task<T>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JobStore>());
    }

    private static TimeSpan DrainTime(WorkerOptions settings)
    {
        var drain = settings.ShutdownTimeout - TimeSpan.FromSeconds(5);
        return drain > TimeSpan.Zero ? drain : TimeSpan.FromSeconds(1);
    }

    private static string? SerializeResult(object? result)
    {
        if (result is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(result, TurnoJson.Options);
        return Encoding.UTF8.GetByteCount(json) <= Limits.MaxResultBytes
            ? json
            : JsonSerializer.Serialize(new { truncated = true, bytes = Encoding.UTF8.GetByteCount(json) }, TurnoJson.Options);
    }

    private sealed class ProgressSink(JobRunner runner, StartedJob started, TimeSpan throttle, TimeProvider clock) : IJobProgressSink
    {
        private long lastTimestamp;
        private int lastValue = -1;

        public async ValueTask ReportAsync(int percent, CancellationToken cancellationToken)
        {
            if (percent == lastValue)
            {
                return;
            }

            if (percent < 100 && lastValue >= 0 && clock.GetElapsedTime(lastTimestamp) < throttle)
            {
                return;
            }

            lastValue = percent;
            lastTimestamp = clock.GetTimestamp();
            await runner.ReportProgressAsync(started, percent, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Lock da tarefa {JobId} está com outro worker; adiada: {Deferred}.")]
    private static partial void LogDuplicate(ILogger logger, Guid jobId, bool deferred);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mensagem antiga para a tarefa {JobId} (despacho {DispatchId}); ignorada.")]
    private static partial void LogStale(ILogger logger, Guid jobId, Guid dispatchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lock distribuído indisponível para a tarefa {JobId}: {Error}")]
    private static partial void LogLockUnavailable(ILogger logger, Guid jobId, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tarefa {JobId} aguardando a chave de concorrência '{Key}'.")]
    private static partial void LogResourceBusy(ILogger logger, Guid jobId, string? key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Iniciando {JobId} ({JobType}), tentativa {Attempt}/{MaxAttempts}.")]
    private static partial void LogStarted(ILogger logger, Guid jobId, string jobType, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tarefa {JobId} ({JobType}) concluída em {ElapsedMs:0} ms.")]
    private static partial void LogSucceeded(ILogger logger, Guid jobId, string jobType, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tarefa {JobId} ({JobType}) falhou na tentativa {Attempt}/{MaxAttempts}; nova tentativa em {RetryAt:o}: {Error}")]
    private static partial void LogRetrying(ILogger logger, Guid jobId, string jobType, int attempt, int maxAttempts, DateTimeOffset? retryAt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tarefa {JobId} ({JobType}) enviada para a fila de erros após {Attempt} tentativa(s).")]
    private static partial void LogDeadLettered(ILogger logger, Exception exception, Guid jobId, string jobType, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tarefa {JobId} ({JobType}) adiada por {Delay}: disjuntor aberto.")]
    private static partial void LogCircuitDeferred(ILogger logger, Guid jobId, string jobType, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tarefa {JobId} cancelada durante a execução.")]
    private static partial void LogCancelled(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tarefa {JobId} interrompida pelo encerramento do worker e devolvida à fila.")]
    private static partial void LogInterrupted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Posse da tarefa {JobId} perdida ({Reason}); o resultado local foi descartado.")]
    private static partial void LogOwnershipLost(ILogger logger, Guid jobId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lock Redis da tarefa {JobId} expirou; a exclusividade segue garantida pelo lease no banco.")]
    private static partial void LogJobLockLost(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao renovar o lease da tarefa {JobId}: {Error}")]
    private static partial void LogRenewFailed(ILogger logger, Guid jobId, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Falha ao registrar o progresso da tarefa {JobId}: {Error}")]
    private static partial void LogProgressFailed(ILogger logger, Guid jobId, string error);
}

public sealed class JobTimeoutException : TimeoutException
{
    public JobTimeoutException()
    {
    }

    public JobTimeoutException(string message) : base(message)
    {
    }

    public JobTimeoutException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
