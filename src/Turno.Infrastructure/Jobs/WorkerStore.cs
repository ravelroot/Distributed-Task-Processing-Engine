using Microsoft.EntityFrameworkCore;
using Turno.Contracts;
using Turno.Core.Domain;
using Turno.Infrastructure.Persistence;

namespace Turno.Infrastructure.Jobs;

public sealed class WorkerStore(TurnoDbContext db, TimeProvider clock)
{
    public async Task RegisterAsync(WorkerNode node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        var now = clock.GetUtcNow();
        var existing = await db.Workers.FirstOrDefaultAsync(w => w.Id == node.Id, cancellationToken);
        if (existing is null)
        {
            node.LastHeartbeatAt = now;
            db.Workers.Add(node);
        }
        else
        {
            existing.HostName = node.HostName;
            existing.ProcessId = node.ProcessId;
            existing.Status = node.Status;
            existing.Queues = node.Queues;
            existing.JobTypes = node.JobTypes;
            existing.Concurrency = node.Concurrency;
            existing.Version = node.Version;
            existing.StartedAt = node.StartedAt;
            existing.LastHeartbeatAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<int> HeartbeatAsync(string workerId, WorkerStatus status, int runningJobs, long processedJobs, long failedJobs, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return db.Workers
            .Where(w => w.Id == workerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.LastHeartbeatAt, now)
                .SetProperty(w => w.Status, status)
                .SetProperty(w => w.RunningJobs, runningJobs)
                .SetProperty(w => w.ProcessedJobs, processedJobs)
                .SetProperty(w => w.FailedJobs, failedJobs), cancellationToken);
    }

    public async Task<IReadOnlyList<WorkerNode>> MarkStaleOfflineAsync(TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        var threshold = clock.GetUtcNow() - staleAfter;
        var stale = await db.Workers
            .Where(w => w.Status != WorkerStatus.Offline && w.LastHeartbeatAt < threshold)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return stale;
        }

        foreach (var node in stale)
        {
            node.Status = WorkerStatus.Offline;
            node.RunningJobs = 0;
        }

        await db.SaveChangesAsync(cancellationToken);
        return stale;
    }
}
