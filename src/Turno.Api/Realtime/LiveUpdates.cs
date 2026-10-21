using System.Threading.Channels;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Turno.Contracts;
using Turno.Contracts.Messages;

namespace Turno.Api.Realtime;

public sealed class JobsHub : Hub;

public sealed partial class LiveBroadcaster(IHubContext<JobsHub> hub, ILogger<LiveBroadcaster> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(300);

    private readonly Channel<JobStatusChanged> jobs = Channel.CreateBounded<JobStatusChanged>(new BoundedChannelOptions(20_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    private readonly Channel<WorkerStatusChanged> workers = Channel.CreateBounded<WorkerStatusChanged>(new BoundedChannelOptions(2_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    public void Publish(JobStatusChanged change) => jobs.Writer.TryWrite(change);

    public void Publish(WorkerStatusChanged change) => workers.Writer.TryWrite(change);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var jobBatch = new Dictionary<Guid, JobStatusChanged>();
        while (jobs.Reader.TryRead(out var change))
        {
            if (!jobBatch.TryGetValue(change.JobId, out var current) || current.OccurredAt <= change.OccurredAt)
            {
                jobBatch[change.JobId] = change;
            }
        }

        var workerBatch = new Dictionary<string, WorkerStatusChanged>(StringComparer.Ordinal);
        while (workers.Reader.TryRead(out var change))
        {
            workerBatch[change.WorkerId] = change;
        }

        try
        {
            if (jobBatch.Count > 0)
            {
                await hub.Clients.All.SendAsync(HubMethods.JobsChanged, jobBatch.Values.OrderBy(c => c.OccurredAt).ToList(), cancellationToken);
            }

            if (workerBatch.Count > 0)
            {
                await hub.Clients.All.SendAsync(HubMethods.WorkersChanged, workerBatch.Values.ToList(), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBroadcastFailed(logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao enviar atualizações em tempo real: {Error}")]
    private static partial void LogBroadcastFailed(ILogger logger, string error);
}

public sealed class JobStatusChangedConsumer(LiveBroadcaster broadcaster) : IConsumer<JobStatusChanged>
{
    public Task Consume(ConsumeContext<JobStatusChanged> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        broadcaster.Publish(context.Message);
        return Task.CompletedTask;
    }
}

public sealed class WorkerStatusChangedConsumer(LiveBroadcaster broadcaster) : IConsumer<WorkerStatusChanged>
{
    public Task Consume(ConsumeContext<WorkerStatusChanged> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        broadcaster.Publish(context.Message);
        return Task.CompletedTask;
    }
}
