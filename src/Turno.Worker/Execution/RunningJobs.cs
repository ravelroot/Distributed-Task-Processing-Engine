using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Turno.Core.Options;

namespace Turno.Worker.Execution;

public enum StopReason
{
    None = 0,
    CancellationRequested = 1,
    LeaseLost = 2,
    LockLost = 3,
    Shutdown = 4
}

public sealed class RunningJob : IDisposable
{
    private int reason;

    internal RunningJob(Guid jobId, string type)
    {
        JobId = jobId;
        Type = type;
        Source = new CancellationTokenSource();
    }

    public Guid JobId { get; }

    public string Type { get; }

    public CancellationTokenSource Source { get; }

    public StopReason Reason => (StopReason)Volatile.Read(ref reason);

    public void Stop(StopReason stopReason, TimeSpan? after = null)
    {
        Interlocked.CompareExchange(ref reason, (int)stopReason, (int)StopReason.None);
        try
        {
            if (after is { } delay && delay > TimeSpan.Zero)
            {
                Source.CancelAfter(delay);
            }
            else
            {
                Source.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => Source.Dispose();
}

public sealed class RunningJobs(IOptions<WorkerOptions> options)
{
    private readonly ConcurrentDictionary<Guid, RunningJob> jobs = new();
    private long processed;
    private long failed;

    public int Count => jobs.Count;

    public long Processed => Interlocked.Read(ref processed);

    public long Failed => Interlocked.Read(ref failed);

    public int Capacity { get; } = options.Value.EffectiveQueues().Sum(q => Math.Max(1, q.Concurrency));

    public RunningJob Track(Guid jobId, string type)
    {
        var job = new RunningJob(jobId, type);
        jobs[jobId] = job;
        return job;
    }

    public void Untrack(RunningJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        jobs.TryRemove(new KeyValuePair<Guid, RunningJob>(job.JobId, job));
    }

    public bool TryStop(Guid jobId, StopReason reason)
    {
        if (!jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        job.Stop(reason);
        return true;
    }

    public void StopAll(StopReason reason, TimeSpan after)
    {
        foreach (var job in jobs.Values)
        {
            job.Stop(reason, after);
        }
    }

    public void RecordSuccess() => Interlocked.Increment(ref processed);

    public void RecordFailure()
    {
        Interlocked.Increment(ref processed);
        Interlocked.Increment(ref failed);
    }
}
