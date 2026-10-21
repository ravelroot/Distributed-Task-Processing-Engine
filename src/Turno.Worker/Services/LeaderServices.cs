using MassTransit;
using Microsoft.Extensions.Options;
using Turno.Contracts;
using Turno.Contracts.Messages;
using Turno.Core.Domain;
using Turno.Core.Locking;
using Turno.Core.Options;
using Turno.Infrastructure.Jobs;

namespace Turno.Worker.Services;

public abstract partial class LeaderService(
    IServiceScopeFactory scopeFactory,
    IDistributedLockProvider locks,
    TimeProvider clock,
    ILogger logger) : BackgroundService
{
    protected IServiceScopeFactory ScopeFactory { get; } = scopeFactory;

    protected ILogger Logger { get; } = logger;

    protected abstract string Role { get; }

    protected abstract TimeSpan Interval { get; }

    protected abstract bool Enabled { get; }

    protected abstract Task RunOnceAsync(JobStore store, IServiceProvider services, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            LogDisabled(Logger, Role);
            return;
        }

        var interval = Interval > TimeSpan.Zero ? Interval : TimeSpan.FromSeconds(5);
        var lockExpiry = TimeSpan.FromTicks(Math.Max(interval.Ticks * 3, TimeSpan.FromSeconds(15).Ticks));
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            do
            {
                try
                {
                    await using var leader = await locks.TryAcquireAsync(LockKeys.Leader(Role), lockExpiry, stoppingToken);
                    if (leader is not null)
                    {
                        await using var scope = ScopeFactory.CreateAsyncScope();
                        var store = scope.ServiceProvider.GetRequiredService<JobStore>();
                        await RunOnceAsync(store, scope.ServiceProvider, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogFailure(Logger, ex, Role);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Serviço de líder '{Role}' desativado por configuração.")]
    private static partial void LogDisabled(ILogger logger, string role);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no ciclo do serviço '{Role}'.")]
    private static partial void LogFailure(ILogger logger, Exception exception, string role);
}

public sealed partial class SchedulerService(
    IServiceScopeFactory scopeFactory,
    IDistributedLockProvider locks,
    IOptions<SchedulerOptions> options,
    TimeProvider clock,
    ILogger<SchedulerService> logger) : LeaderService(scopeFactory, locks, clock, logger)
{
    private const int MaxBatchesPerTick = 10;

    protected override string Role => "scheduler";

    protected override TimeSpan Interval => options.Value.Interval;

    protected override bool Enabled => options.Value.Enabled;

    protected override async Task RunOnceAsync(JobStore store, IServiceProvider services, CancellationToken cancellationToken)
    {
        var batch = Math.Max(1, options.Value.BatchSize);
        var total = 0;
        for (var i = 0; i < MaxBatchesPerTick; i++)
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var count = await scope.ServiceProvider.GetRequiredService<JobStore>().EnqueueDueAsync(batch, cancellationToken);
            total += count;
            if (count < batch)
            {
                break;
            }
        }

        var recurring = await store.TriggerDueRecurringAsync(50, cancellationToken);
        if (total > 0 || recurring > 0)
        {
            LogTick(Logger, total, recurring);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agendador: {Enqueued} tarefa(s) enviadas para a fila e {Recurring} disparo(s) recorrente(s).")]
    private static partial void LogTick(ILogger logger, int enqueued, int recurring);
}

public sealed partial class RecoveryService(
    IServiceScopeFactory scopeFactory,
    IDistributedLockProvider locks,
    IOptions<RecoveryOptions> options,
    IOptions<RetentionOptions> retention,
    IBus bus,
    TimeProvider clock,
    ILogger<RecoveryService> logger) : LeaderService(scopeFactory, locks, clock, logger)
{
    protected override string Role => "recovery";

    protected override TimeSpan Interval => options.Value.Interval;

    protected override bool Enabled => options.Value.Enabled;

    protected override async Task RunOnceAsync(JobStore store, IServiceProvider services, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var recovered = await store.RecoverExpiredLeasesAsync(settings.LeaseGracePeriod, Math.Max(1, settings.BatchSize), cancellationToken);
        if (recovered > 0)
        {
            LogRecovered(Logger, recovered);
        }

        var redispatched = await store.RedispatchStaleEnqueuedAsync(settings.RedispatchEnqueuedAfter, Math.Max(1, settings.BatchSize), cancellationToken);
        if (redispatched > 0)
        {
            LogRedispatched(Logger, redispatched);
        }

        var stale = await services.GetRequiredService<WorkerStore>().MarkStaleOfflineAsync(retention.Value.StaleWorkerAfter, cancellationToken);
        foreach (var node in stale)
        {
            LogWorkerOffline(Logger, node.Id, node.LastHeartbeatAt);
            await bus.Publish(
                new WorkerStatusChanged(node.Id, node.HostName, WorkerStatus.Offline, node.Queues, node.Concurrency, 0, node.ProcessedJobs, node.FailedJobs, node.LastHeartbeatAt),
                cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recuperação: {Count} tarefa(s) de workers que pararam de responder foram tratadas.")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recuperação: {Count} tarefa(s) paradas na fila tiveram a mensagem reenviada.")]
    private static partial void LogRedispatched(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Worker {WorkerId} sem sinal desde {LastHeartbeat:o}; marcado como offline.")]
    private static partial void LogWorkerOffline(ILogger logger, string workerId, DateTimeOffset lastHeartbeat);
}

public sealed partial class RetentionService(
    IServiceScopeFactory scopeFactory,
    IDistributedLockProvider locks,
    IOptions<RetentionOptions> options,
    TimeProvider clock,
    ILogger<RetentionService> logger) : LeaderService(scopeFactory, locks, clock, logger)
{
    protected override string Role => "retention";

    protected override TimeSpan Interval => options.Value.Interval;

    protected override bool Enabled => options.Value.Enabled;

    protected override async Task RunOnceAsync(JobStore store, IServiceProvider services, CancellationToken cancellationToken)
    {
        var result = await store.ApplyRetentionAsync(options.Value, cancellationToken);
        if (result.SucceededJobs + result.CancelledJobs + result.FailedJobs + result.OfflineWorkers > 0)
        {
            LogCleaned(Logger, result.SucceededJobs, result.CancelledJobs, result.FailedJobs, result.OfflineWorkers);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retenção: removidas {Succeeded} concluídas, {Cancelled} canceladas, {Failed} com falha e {Workers} worker(s) offline.")]
    private static partial void LogCleaned(ILogger logger, int succeeded, int cancelled, int failed, int workers);
}
