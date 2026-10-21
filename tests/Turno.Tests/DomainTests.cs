using Turno.Contracts;
using Turno.Core.Domain;
using Turno.Core.Options;
using Turno.Core.Scheduling;

namespace Turno.Tests;

public class JobStateMachineTests
{
    [Theory]
    [InlineData(JobState.Scheduled, JobState.Enqueued)]
    [InlineData(JobState.Enqueued, JobState.Processing)]
    [InlineData(JobState.Processing, JobState.Succeeded)]
    [InlineData(JobState.Processing, JobState.Scheduled)]
    [InlineData(JobState.Processing, JobState.Enqueued)]
    [InlineData(JobState.Processing, JobState.Failed)]
    [InlineData(JobState.Failed, JobState.Enqueued)]
    [InlineData(JobState.Cancelled, JobState.Enqueued)]
    public void Allows_expected_transitions(JobState from, JobState to) =>
        Assert.True(JobStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(JobState.Succeeded, JobState.Enqueued)]
    [InlineData(JobState.Succeeded, JobState.Processing)]
    [InlineData(JobState.Scheduled, JobState.Processing)]
    [InlineData(JobState.Failed, JobState.Processing)]
    [InlineData(JobState.Cancelled, JobState.Succeeded)]
    public void Rejects_invalid_transitions(JobState from, JobState to)
    {
        Assert.False(JobStateMachine.CanTransition(from, to));
        var error = Assert.Throws<InvalidJobTransitionException>(() => JobStateMachine.EnsureTransition(from, to));
        Assert.Equal(from, error.From);
        Assert.Equal(to, error.To);
    }

    [Fact]
    public void Terminal_states_cannot_be_cancelled_but_can_be_deleted()
    {
        foreach (var state in JobStateMachine.TerminalStates)
        {
            Assert.True(JobStateMachine.IsTerminal(state));
            Assert.False(JobStateMachine.CanCancel(state));
            Assert.True(JobStateMachine.CanDelete(state));
        }

        Assert.False(JobStateMachine.CanDelete(JobState.Processing));
        Assert.True(JobStateMachine.CanRequeue(JobState.Failed));
        Assert.False(JobStateMachine.CanRequeue(JobState.Succeeded));
    }
}

public class NamesTests
{
    [Theory]
    [InlineData("report.generate", true)]
    [InlineData("a", true)]
    [InlineData("file_process-v2", true)]
    [InlineData("Report.Generate", false)]
    [InlineData(".hidden", false)]
    [InlineData("com espaco", false)]
    [InlineData("", false)]
    public void Validates_job_types(string value, bool expected) =>
        Assert.Equal(expected, JobTypeNames.IsValid(value));

    [Theory]
    [InlineData(null, "default")]
    [InlineData("  ", "default")]
    [InlineData(" Critical ", "critical")]
    public void Normalizes_queue_names(string? value, string expected) =>
        Assert.Equal(expected, QueueNames.Normalize(value));

    [Theory]
    [InlineData("reports", true)]
    [InlineData("high-priority", true)]
    [InlineData("com.ponto", false)]
    [InlineData("-inicio", false)]
    public void Validates_queue_names(string value, bool expected) =>
        Assert.Equal(expected, QueueNames.IsValid(value));

    [Fact]
    public void Builds_lock_keys()
    {
        var id = Guid.Parse("7f1c2b0e-9c1d-4c55-9a52-2a6fb0e2f001");
        Assert.Equal("job:7f1c2b0e9c1d4c559a522a6fb0e2f001", LockKeys.Job(id));
        Assert.Equal("resource:cliente-42", LockKeys.Resource(" Cliente-42 "));
        Assert.Equal("leader:scheduler", LockKeys.Leader("scheduler"));
    }
}

public class RetryBackoffTests
{
    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    public void Doubles_the_delay_without_jitter(int attempt, int expectedSeconds)
    {
        var delay = RetryBackoff.Compute(attempt, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), 0, 0.5);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void Caps_at_the_maximum_delay()
    {
        var delay = RetryBackoff.Compute(30, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), 0, 0.5);
        Assert.Equal(TimeSpan.FromMinutes(5), delay);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.999)]
    public void Keeps_jitter_inside_bounds(double sample)
    {
        var delay = RetryBackoff.Compute(2, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), 0.2, sample);
        Assert.InRange(delay.TotalSeconds, 16, 24);
    }

    [Fact]
    public void Handles_zero_and_negative_attempts()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), RetryBackoff.Compute(0, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), 0, 0.5));
        Assert.Equal(TimeSpan.Zero, RetryBackoff.Compute(3, TimeSpan.Zero, TimeSpan.FromHours(1), 0.2, 0.5));
    }
}

public class CronScheduleTests
{
    [Fact]
    public void Computes_next_occurrence_in_the_configured_time_zone()
    {
        var schedule = CronSchedule.Parse("0 6 * * *", "America/Sao_Paulo");
        var from = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var next = schedule.GetNextOccurrence(from);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Supports_seconds_field()
    {
        var schedule = CronSchedule.Parse("*/10 * * * * *", null);
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 3, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 10, TimeSpan.Zero), schedule.GetNextOccurrence(from));
    }

    [Fact]
    public void Lists_consecutive_occurrences()
    {
        var schedule = CronSchedule.Parse("*/15 * * * *", "UTC");
        var occurrences = schedule.GetOccurrences(new DateTimeOffset(2026, 1, 1, 10, 1, 0, TimeSpan.Zero), 3).ToList();
        Assert.Equal([15, 30, 45], occurrences.Select(o => o.Minute));
    }

    [Theory]
    [InlineData("isso não é cron", "UTC")]
    [InlineData("61 * * * *", "UTC")]
    [InlineData("0 6 * * *", "Fuso/Inexistente")]
    [InlineData("", "UTC")]
    public void Rejects_invalid_definitions(string cron, string zone)
    {
        Assert.False(CronSchedule.TryParse(cron, zone, out var schedule, out var error));
        Assert.Null(schedule);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

public class WorkerOptionsTests
{
    [Fact]
    public void Uses_default_queue_when_none_is_configured()
    {
        var queues = new WorkerOptions().EffectiveQueues();
        var queue = Assert.Single(queues);
        Assert.Equal(QueueNames.Default, queue.Name);
        Assert.Equal(4, queue.Concurrency);
    }

    [Fact]
    public void Merges_duplicate_queues_and_normalizes_names()
    {
        var options = new WorkerOptions
        {
            Queues =
            [
                new QueueOptions { Name = "Reports", Concurrency = 2 },
                new QueueOptions { Name = "reports", Concurrency = 5 },
                new QueueOptions { Name = "critical", Concurrency = 1 }
            ]
        };

        var queues = options.EffectiveQueues();
        Assert.Equal(2, queues.Count);
        Assert.Equal(5, queues.Single(q => q.Name == "reports").Concurrency);
    }

    [Fact]
    public void Resolves_per_type_resilience_overrides()
    {
        var options = new ResilienceOptions
        {
            DefaultMaxAttempts = 5,
            Types = new Dictionary<string, JobTypeResilienceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["integration.sync"] = new() { MaxAttempts = 9, ImmediateRetries = 0, Timeout = TimeSpan.FromSeconds(30) }
            }
        };

        Assert.Equal(9, options.MaxAttemptsFor("integration.sync"));
        Assert.Equal(0, options.ImmediateRetriesFor("integration.sync"));
        Assert.Equal(TimeSpan.FromSeconds(30), options.TimeoutFor("integration.sync"));
        Assert.Equal(5, options.MaxAttemptsFor("report.generate"));
    }
}
