using Microsoft.EntityFrameworkCore;
using Npgsql;
using Turno.Contracts;
using Turno.Contracts.Api;
using Turno.Core.Domain;
using Turno.Infrastructure.Options;
using Turno.Infrastructure.Persistence;

namespace Turno.Infrastructure.Jobs;

public sealed record JobListFilter(JobState? State, string? Type, string? Queue, string? Search, Guid? RecurringJobId, int Page, int PageSize);

public sealed class JobQueries(TurnoDbContext db, TimeProvider clock)
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(30);

    public async Task<PagedResult<JobSummary>> ListJobsAsync(JobListFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, Limits.MaxPageSize);
        var query = db.Jobs.AsNoTracking();

        if (filter.State is { } state)
        {
            query = query.Where(j => j.State == state);
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            var type = JobTypeNames.Normalize(filter.Type);
            query = query.Where(j => j.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(filter.Queue))
        {
            var queue = QueueNames.Normalize(filter.Queue);
            query = query.Where(j => j.Queue == queue);
        }

        if (filter.RecurringJobId is { } recurringId)
        {
            query = query.Where(j => j.RecurringJobId == recurringId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            if (Guid.TryParse(term, out var id))
            {
                query = query.Where(j => j.Id == id);
            }
            else
            {
                var pattern = $"%{term.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
                query = query.Where(j => EF.Functions.ILike(j.Type, pattern) || EF.Functions.ILike(j.WorkerId!, pattern) || EF.Functions.ILike(j.Id.ToString(), pattern));
            }
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(j => new JobSummary(j.Id, j.Type, j.Queue, j.State, j.Attempt, j.MaxAttempts, j.Progress, j.WorkerId, j.CreatedAt, j.ScheduledAt, j.StartedAt, j.FinishedAt, j.LastError, j.CancellationRequested))
            .ToListAsync(cancellationToken);

        return new PagedResult<JobSummary>(items, page, size, total);
    }

    public async Task<JobSummary?> GetSummaryAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Jobs.AsNoTracking()
            .Where(j => j.Id == id)
            .Select(j => new JobSummary(j.Id, j.Type, j.Queue, j.State, j.Attempt, j.MaxAttempts, j.Progress, j.WorkerId, j.CreatedAt, j.ScheduledAt, j.StartedAt, j.FinishedAt, j.LastError, j.CancellationRequested))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<JobDetails?> GetJobAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == id)
            .Select(j => new
            {
                Summary = new JobSummary(j.Id, j.Type, j.Queue, j.State, j.Attempt, j.MaxAttempts, j.Progress, j.WorkerId, j.CreatedAt, j.ScheduledAt, j.StartedAt, j.FinishedAt, j.LastError, j.CancellationRequested),
                j.Payload,
                j.Result,
                j.IdempotencyKey,
                j.ConcurrencyKey,
                j.TimeoutSeconds,
                j.RecurringJobId,
                RecurringName = j.RecurringJob != null ? j.RecurringJob.Name : null,
                j.EnqueuedAt,
                j.LeaseExpiresAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null)
        {
            return null;
        }

        var attempts = await db.JobAttempts.AsNoTracking()
            .Where(a => a.JobId == id)
            .OrderBy(a => a.StartedAt)
            .ThenBy(a => a.Id)
            .Select(a => new JobAttemptDto(a.Number, a.WorkerId, a.Outcome, a.StartedAt, a.FinishedAt, a.DurationMs, a.ErrorType, a.ErrorMessage))
            .ToListAsync(cancellationToken);

        var history = await db.JobStateChanges.AsNoTracking()
            .Where(h => h.JobId == id)
            .OrderBy(h => h.Id)
            .Select(h => new JobStateChangeDto(h.FromState, h.ToState, h.Reason, h.WorkerId, h.OccurredAt))
            .ToListAsync(cancellationToken);

        return new JobDetails(job.Summary, job.Payload, job.Result, job.IdempotencyKey, job.ConcurrencyKey, job.TimeoutSeconds, job.RecurringJobId, job.RecurringName, job.EnqueuedAt, job.LeaseExpiresAt, attempts, history);
    }

    public async Task<StatsResponse> GetStatsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var lastDay = now.AddHours(-24);
        var lastHour = now.AddHours(-1);
        var onlineSince = now - OnlineWindow;

        var counts = await db.Jobs.AsNoTracking()
            .GroupBy(j => j.State)
            .Select(g => new { State = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.State, x => x.Count, cancellationToken);

        long Count(JobState state) => counts.TryGetValue(state, out var value) ? value : 0;

        var succeeded = await db.Jobs.AsNoTracking()
            .LongCountAsync(j => j.State == JobState.Succeeded && j.FinishedAt >= lastDay, cancellationToken);

        var failedAttempts = await db.JobAttempts.AsNoTracking()
            .LongCountAsync(a => (a.Outcome == AttemptOutcome.Failed || a.Outcome == AttemptOutcome.Lost) && a.FinishedAt >= lastDay, cancellationToken);

        var averageDuration = await db.JobAttempts.AsNoTracking()
            .Where(a => a.Outcome == AttemptOutcome.Succeeded && a.FinishedAt >= lastHour)
            .AverageAsync(a => (double?)a.DurationMs, cancellationToken);

        var online = await db.Workers.AsNoTracking()
            .Where(w => w.Status == WorkerStatus.Online && w.LastHeartbeatAt >= onlineSince)
            .Select(w => w.Concurrency)
            .ToListAsync(cancellationToken);

        var pendingDeadLetters = await db.DeadLetters.AsNoTracking()
            .LongCountAsync(d => d.RequeuedAt == null, cancellationToken);

        var queues = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Scheduled || j.State == JobState.Enqueued || j.State == JobState.Processing)
            .GroupBy(j => new { j.Queue, j.State })
            .Select(g => new { g.Key.Queue, g.Key.State, Count = g.LongCount() })
            .ToListAsync(cancellationToken);

        var queueStats = queues
            .GroupBy(q => q.Queue)
            .Select(g => new QueueStats(
                g.Key,
                g.Where(x => x.State == JobState.Scheduled).Sum(x => x.Count),
                g.Where(x => x.State == JobState.Enqueued).Sum(x => x.Count),
                g.Where(x => x.State == JobState.Processing).Sum(x => x.Count)))
            .OrderBy(q => q.Queue, StringComparer.Ordinal)
            .ToList();

        var throughput = await GetThroughputAsync(now, cancellationToken);

        return new StatsResponse(
            new StateCounts(Count(JobState.Scheduled), Count(JobState.Enqueued), Count(JobState.Processing), Count(JobState.Succeeded), Count(JobState.Failed), Count(JobState.Cancelled)),
            succeeded,
            failedAttempts,
            averageDuration,
            online.Count,
            online.Sum(),
            pendingDeadLetters,
            throughput,
            queueStats,
            now);
    }

    public async Task<IReadOnlyList<WorkerDto>> ListWorkersAsync(CancellationToken cancellationToken)
    {
        var staleBefore = clock.GetUtcNow() - OnlineWindow;
        var workers = await db.Workers.AsNoTracking().ToListAsync(cancellationToken);

        return workers
            .Select(w => new WorkerDto(
                w.Id,
                w.HostName,
                w.ProcessId,
                w.Status != WorkerStatus.Offline && w.LastHeartbeatAt < staleBefore ? WorkerStatus.Offline : w.Status,
                w.Queues,
                w.JobTypes,
                w.Concurrency,
                w.RunningJobs,
                w.ProcessedJobs,
                w.FailedJobs,
                w.StartedAt,
                w.LastHeartbeatAt,
                w.Version))
            .OrderBy(w => w.Status switch
            {
                WorkerStatus.Online => 0,
                WorkerStatus.Stopping => 1,
                _ => 2
            })
            .ThenBy(w => w.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListKnownJobTypesAsync(CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - OnlineWindow;
        var types = await db.Workers.AsNoTracking()
            .Where(w => w.Status == WorkerStatus.Online && w.LastHeartbeatAt >= since)
            .Select(w => w.JobTypes)
            .ToListAsync(cancellationToken);
        return types.SelectMany(t => t).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<RecurringJobDto>> ListRecurringAsync(CancellationToken cancellationToken) =>
        await (from r in db.RecurringJobs.AsNoTracking()
               join j in db.Jobs.AsNoTracking() on r.LastJobId equals (Guid?)j.Id into last
               from j in last.DefaultIfEmpty()
               orderby r.Name
               select new RecurringJobDto(
                   r.Id,
                   r.Name,
                   r.Type,
                   r.Queue,
                   r.Cron,
                   r.TimeZone,
                   r.Payload,
                   r.Enabled,
                   r.AllowConcurrent,
                   r.MaxAttempts,
                   r.TimeoutSeconds,
                   r.NextRunAt,
                   r.LastRunAt,
                   r.LastJobId,
                   j == null ? (JobState?)null : j.State,
                   r.UpdatedAt))
            .ToListAsync(cancellationToken);

    public async Task<PagedResult<DeadLetterDto>> ListDeadLettersAsync(bool pendingOnly, int page, int pageSize, CancellationToken cancellationToken)
    {
        var current = Math.Max(1, page);
        var size = Math.Clamp(pageSize, 1, Limits.MaxPageSize);
        var query = db.DeadLetters.AsNoTracking();
        if (pendingOnly)
        {
            query = query.Where(d => d.RequeuedAt == null);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(d => d.FailedAt)
            .Skip((current - 1) * size)
            .Take(size)
            .Select(d => new DeadLetterDto(d.Id, d.JobId, d.Type, d.Queue, d.Attempts, d.ErrorType, d.ErrorMessage, d.StackTrace, d.WorkerId, d.FailedAt, d.RequeuedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<DeadLetterDto>(items, current, size, total);
    }

    private async Task<IReadOnlyList<ThroughputPoint>> GetThroughputAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var end = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        var start = end.AddMinutes(-59);
        const string sql =
            "SELECT date_trunc('minute', finished_at) AS \"Minute\", " +
            "count(*) FILTER (WHERE outcome = 'Succeeded')::int AS \"Succeeded\", " +
            "count(*) FILTER (WHERE outcome IN ('Failed', 'Lost'))::int AS \"Failed\" " +
            "FROM " + DatabaseOptions.Schema + ".job_attempts WHERE finished_at >= @start GROUP BY 1";

        var rows = await db.Database
            .SqlQueryRaw<ThroughputRow>(sql, new NpgsqlParameter("start", start))
            .ToListAsync(cancellationToken);

        var byMinute = rows.ToDictionary(r => r.Minute.ToUniversalTime(), r => r);
        var points = new List<ThroughputPoint>(60);
        for (var minute = start; minute <= end; minute = minute.AddMinutes(1))
        {
            points.Add(byMinute.TryGetValue(minute, out var row)
                ? new ThroughputPoint(minute, row.Succeeded, row.Failed)
                : new ThroughputPoint(minute, 0, 0));
        }

        return points;
    }

    private sealed class ThroughputRow
    {
        public DateTimeOffset Minute { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
    }
}
