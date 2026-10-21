using MassTransit;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Turno.Contracts;
using Turno.Contracts.Messages;
using Turno.Core.Options;
using Turno.Infrastructure.Jobs;
using Turno.Worker.Execution;

namespace Turno.Worker.Services;

public sealed partial class WorkerRegistrationService(
    IServiceScopeFactory scopeFactory,
    WorkerIdentity identity,
    ILogger<WorkerRegistrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
                MaxRetryAttempts = 10,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                MaxDelay = TimeSpan.FromSeconds(10),
                UseJitter = true
            })
            .Build();

        await pipeline.ExecuteAsync(async token =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WorkerStore>().RegisterAsync(identity.ToNode(WorkerStatus.Online), token);
        }, cancellationToken);

        LogRegistered(logger, identity.Id, string.Join(", ", identity.Queues), identity.Concurrency, string.Join(", ", identity.JobTypes));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WorkerStore>().HeartbeatAsync(identity.Id, WorkerStatus.Offline, 0, 0, 0, cancellationToken);
            LogStopped(logger, identity.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStopFailed(logger, identity.Id, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker {WorkerId} registrado. Filas: {Queues}. Concorrência: {Concurrency}. Tipos: {Types}.")]
    private static partial void LogRegistered(ILogger logger, string workerId, string queues, int concurrency, string types);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker {WorkerId} marcado como offline.")]
    private static partial void LogStopped(ILogger logger, string workerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Não foi possível marcar o worker {WorkerId} como offline: {Error}")]
    private static partial void LogStopFailed(ILogger logger, string workerId, string error);
}

public sealed partial class HeartbeatService(
    IServiceScopeFactory scopeFactory,
    WorkerIdentity identity,
    RunningJobs running,
    IBus bus,
    IOptions<WorkerOptions> options,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.HeartbeatInterval > TimeSpan.Zero ? options.Value.HeartbeatInterval : TimeSpan.FromSeconds(5);
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            do
            {
                await BeatAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        var status = lifetime.ApplicationStopping.IsCancellationRequested ? WorkerStatus.Stopping : WorkerStatus.Online;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<WorkerStore>();
            var updated = await store.HeartbeatAsync(identity.Id, status, running.Count, running.Processed, running.Failed, cancellationToken);
            if (updated == 0)
            {
                var node = identity.ToNode(status);
                node.RunningJobs = running.Count;
                node.ProcessedJobs = running.Processed;
                node.FailedJobs = running.Failed;
                await store.RegisterAsync(node, cancellationToken);
            }

            await bus.Publish(
                new WorkerStatusChanged(identity.Id, identity.HostName, status, identity.Queues, identity.Concurrency, running.Count, running.Processed, running.Failed, clock.GetUtcNow()),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHeartbeatFailed(logger, identity.Id, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha no heartbeat do worker {WorkerId}: {Error}")]
    private static partial void LogHeartbeatFailed(ILogger logger, string workerId, string error);
}
