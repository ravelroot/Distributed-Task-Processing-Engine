using MassTransit;
using Turno.Contracts.Messages;

namespace Turno.Worker.Execution;

public sealed class ExecuteJobConsumer(JobRunner runner) : IConsumer<ExecuteJob>
{
    public Task Consume(ConsumeContext<ExecuteJob> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return runner.RunAsync(context.Message, context.CancellationToken);
    }
}

public sealed class JobCancellationConsumer(RunningJobs running) : IConsumer<JobCancellationRequested>
{
    public Task Consume(ConsumeContext<JobCancellationRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        running.TryStop(context.Message.JobId, StopReason.CancellationRequested);
        return Task.CompletedTask;
    }
}
