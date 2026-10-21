using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Turno.Contracts;
using Turno.Contracts.Api;
using Turno.Core.Domain;
using Turno.Core.Handlers;
using Turno.Core.Options;
using Turno.Infrastructure.Jobs;
using Turno.Infrastructure.Persistence;
using Turno.Infrastructure.Resilience;
using Turno.Worker.Execution;

namespace Turno.Tests;

public class SnakeCaseNamingTests
{
    [Theory]
    [InlineData("IdempotencyKey", "idempotency_key")]
    [InlineData("PK_jobs", "pk_jobs")]
    [InlineData("InboxState", "inbox_state")]
    [InlineData("IX_jobs_State_ScheduledAt", "ix_jobs_state_scheduled_at")]
    [InlineData("xmin", "xmin")]
    [InlineData("HTTPStatus", "http_status")]
    [InlineData("LastSequenceNumber", "last_sequence_number")]
    public void Converts_names(string input, string expected) =>
        Assert.Equal(expected, SnakeCaseNaming.ToSnakeCase(input));
}

public class DatabaseModelTests
{
    private static TurnoDbContext CreateContext() => new TurnoDbContextFactory().CreateDbContext([]);

    [Fact]
    public void Maps_every_table_into_the_turno_schema_with_snake_case()
    {
        using var db = CreateContext();
        var tables = db.Model.GetEntityTypes().Select(e => (e.GetSchema(), e.GetTableName())).ToList();

        Assert.All(tables, t => Assert.Equal("turno", t.Item1));
        Assert.Contains(("turno", "jobs"), tables);
        Assert.Contains(("turno", "job_attempts"), tables);
        Assert.Contains(("turno", "job_state_changes"), tables);
        Assert.Contains(("turno", "recurring_jobs"), tables);
        Assert.Contains(("turno", "workers"), tables);
        Assert.Contains(("turno", "dead_letters"), tables);
        Assert.Contains(("turno", "outbox_message"), tables);
        Assert.Contains(("turno", "outbox_state"), tables);
        Assert.Contains(("turno", "inbox_state"), tables);

        foreach (var property in db.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            var column = property.GetColumnName();
            Assert.Equal(column, column.ToLowerInvariant());
        }
    }

    [Fact]
    public void Uses_postgres_native_types()
    {
        using var db = CreateContext();
        var job = db.Model.FindEntityType(typeof(Job))!;
        Assert.Equal("jsonb", job.FindProperty(nameof(Job.Payload))!.GetColumnType());
        var worker = db.Model.FindEntityType(typeof(WorkerNode))!;
        Assert.Equal("text[]", worker.FindProperty(nameof(WorkerNode.Queues))!.GetColumnType());
        var recurring = db.Model.FindEntityType(typeof(RecurringJob))!;
        Assert.Equal("xmin", recurring.FindProperty(nameof(RecurringJob.Version))!.GetColumnName());
    }

    [Fact]
    public void Generates_schema_script_without_a_server()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE turno.jobs", script, StringComparison.Ordinal);
        Assert.Contains("ix_jobs_state_scheduled_at", script, StringComparison.Ordinal);
        Assert.Contains("fk_job_attempts_jobs_job_id", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"xmin\" xid", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrations_match_the_model()
    {
        using var db = CreateContext();
        Assert.False(db.Database.HasPendingModelChanges(), "Gere uma nova migração: o modelo mudou desde a última.");
    }
}

public class JobRequestMapperTests
{
    private static readonly ResilienceOptions Defaults = new() { DefaultMaxAttempts = 4 };

    [Fact]
    public void Maps_a_valid_request()
    {
        using var payload = JsonDocument.Parse("""{"rows": 10}""");
        var request = new EnqueueJobRequest
        {
            Type = " Report.Generate ",
            Queue = "Reports",
            Payload = payload.RootElement.Clone(),
            DelaySeconds = 60,
            ConcurrencyKey = " cliente-1 "
        };

        var submission = JobRequestMapper.TryMap(request, "chave-123", Defaults, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(submission);
        Assert.Equal("report.generate", submission.Type);
        Assert.Equal("reports", submission.Queue);
        Assert.Equal("""{"rows": 10}""", submission.Payload);
        Assert.Equal(4, submission.MaxAttempts);
        Assert.Equal("chave-123", submission.IdempotencyKey);
        Assert.Equal("cliente-1", submission.ConcurrencyKey);
        Assert.NotNull(submission.RunAt);
        Assert.InRange((submission.RunAt.Value - DateTimeOffset.UtcNow).TotalSeconds, 55, 61);
    }

    [Fact]
    public void Reports_every_invalid_field()
    {
        var request = new EnqueueJobRequest
        {
            Type = "Tipo Inválido",
            Queue = "fila com espaço",
            RunAt = DateTimeOffset.UtcNow,
            DelaySeconds = 5,
            MaxAttempts = 0,
            TimeoutSeconds = -1
        };

        var submission = JobRequestMapper.TryMap(request, null, Defaults, out var errors);

        Assert.Null(submission);
        Assert.Contains("type", errors.Keys);
        Assert.Contains("queue", errors.Keys);
        Assert.Contains("runAt", errors.Keys);
        Assert.Contains("maxAttempts", errors.Keys);
        Assert.Contains("timeoutSeconds", errors.Keys);
    }

    [Fact]
    public void Rejects_oversized_payload()
    {
        var big = JsonSerializer.SerializeToElement(new { data = new string('x', Limits.MaxPayloadBytes) });
        var submission = JobRequestMapper.TryMap(new EnqueueJobRequest { Type = "file.process", Payload = big }, null, Defaults, out var errors);
        Assert.Null(submission);
        Assert.Contains("payload", errors.Keys);
    }

    [Fact]
    public void Maps_recurring_definitions_and_validates_cron()
    {
        var valid = JobRequestMapper.TryMap(new UpsertRecurringJobRequest { Name = "Relatorio-Diario", Type = "report.generate", Cron = "0  6 * * *", TimeZone = "America/Sao_Paulo" }, Defaults, out var none);
        Assert.Empty(none);
        Assert.NotNull(valid);
        Assert.Equal("relatorio-diario", valid.Name);
        Assert.Equal("0 6 * * *", valid.Cron);

        var invalid = JobRequestMapper.TryMap(new UpsertRecurringJobRequest { Name = "x", Type = "report.generate", Cron = "todo dia" }, Defaults, out var errors);
        Assert.Null(invalid);
        Assert.Contains("cron", errors.Keys);
    }
}

public class ConfigurationJsonTests
{
    [Fact]
    public void Converts_sections_to_typed_json()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payload:rows"] = "250000",
                ["Payload:ratio"] = "0.25",
                ["Payload:enabled"] = "true",
                ["Payload:name"] = "relatório",
                ["Payload:tags:0"] = "a",
                ["Payload:tags:1"] = "b"
            })
            .Build();

        var element = ConfigurationJson.ToElement(configuration.GetSection("Payload"));

        Assert.NotNull(element);
        var root = element.Value;
        Assert.Equal(250000, root.GetProperty("rows").GetInt64());
        Assert.Equal(0.25, root.GetProperty("ratio").GetDouble());
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("relatório", root.GetProperty("name").GetString());
        Assert.Equal(["a", "b"], root.GetProperty("tags").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Missing_section_returns_null()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Null(ConfigurationJson.ToElement(configuration.GetSection("Payload")));
    }
}

public class JobResiliencePipelinesTests
{
    private static JobResiliencePipelines Create(Action<ResilienceOptions> configure)
    {
        var options = new ResilienceOptions { ImmediateRetryDelay = TimeSpan.FromMilliseconds(1) };
        configure(options);
        return new JobResiliencePipelines(new StaticMonitor<ResilienceOptions>(options), NullLogger<JobResiliencePipelines>.Instance, TimeProvider.System);
    }

    [Fact]
    public async Task Retries_transient_failures_immediately()
    {
        using var pipelines = Create(o =>
        {
            o.ImmediateRetries = 2;
            o.CircuitBreaker.Enabled = false;
        });
        var calls = 0;

        await pipelines.Get("test.flaky").ExecuteAsync(_ =>
        {
            calls++;
            return calls < 3 ? ValueTask.FromException(new HttpRequestException("falha passageira")) : ValueTask.CompletedTask;
        });

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Does_not_retry_permanent_failures()
    {
        using var pipelines = Create(o => o.ImmediateRetries = 3);
        var calls = 0;

        await Assert.ThrowsAsync<PermanentJobException>(async () => await pipelines.Get("test.permanent").ExecuteAsync(_ =>
        {
            calls++;
            return ValueTask.FromException(new PermanentJobException("dados inválidos"));
        }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Opens_the_circuit_after_repeated_failures()
    {
        using var pipelines = Create(o =>
        {
            o.ImmediateRetries = 0;
            o.CircuitBreaker = new CircuitBreakerOptions
            {
                Enabled = true,
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30)
            };
        });
        var pipeline = pipelines.Get("test.broken");

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromException(new InvalidOperationException("serviço fora"))));
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.CompletedTask));
        Assert.Equal(TimeSpan.FromSeconds(30), pipelines.BreakDurationFor("test.broken"));
    }

    [Fact]
    public async Task Applies_the_job_timeout()
    {
        using var pipelines = Create(o => o.CircuitBreaker.Enabled = false);
        var context = ResilienceContextPool.Shared.Get(CancellationToken.None);
        context.Properties.Set(JobResiliencePipelines.TimeoutKey, TimeSpan.FromSeconds(1));
        try
        {
            await Assert.ThrowsAsync<TimeoutRejectedException>(async () =>
                await pipelines.Get("test.slow").ExecuteAsync(async ctx => await Task.Delay(TimeSpan.FromSeconds(10), ctx.CancellationToken), context));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    [Fact]
    public void Prefers_job_specific_timeout()
    {
        using var pipelines = Create(o => o.DefaultTimeout = TimeSpan.FromMinutes(3));
        Assert.Equal(TimeSpan.FromSeconds(45), pipelines.TimeoutFor("any", 45));
        Assert.Equal(TimeSpan.FromMinutes(3), pipelines.TimeoutFor("any", null));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), true)]
    [InlineData(typeof(TimeoutException), true)]
    [InlineData(typeof(PermanentJobException), false)]
    [InlineData(typeof(OperationCanceledException), false)]
    [InlineData(typeof(TaskCanceledException), false)]
    public void Classifies_transient_exceptions(Type type, bool expected)
    {
        var exception = (Exception)Activator.CreateInstance(type)!;
        Assert.Equal(expected, JobResiliencePipelines.IsTransient(exception));
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

public class RunningJobsTests
{
    [Fact]
    public void Keeps_the_first_stop_reason()
    {
        var running = new RunningJobs(Options.Create(new WorkerOptions()));
        using var job = running.Track(Guid.NewGuid(), "test");

        job.Stop(StopReason.CancellationRequested);
        job.Stop(StopReason.Shutdown);

        Assert.Equal(StopReason.CancellationRequested, job.Reason);
        Assert.True(job.Source.IsCancellationRequested);
    }

    [Fact]
    public void Stops_only_tracked_jobs_and_counts_outcomes()
    {
        var running = new RunningJobs(Options.Create(new WorkerOptions()));
        using var job = running.Track(Guid.NewGuid(), "test");

        Assert.True(running.TryStop(job.JobId, StopReason.LeaseLost));
        Assert.False(running.TryStop(Guid.NewGuid(), StopReason.LeaseLost));
        Assert.Equal(1, running.Count);

        running.Untrack(job);
        running.RecordSuccess();
        running.RecordFailure();

        Assert.Equal(0, running.Count);
        Assert.Equal(2, running.Processed);
        Assert.Equal(1, running.Failed);
        Assert.Equal(4, running.Capacity);
    }
}

public class StateCountsTests
{
    [Fact]
    public void Sums_and_reads_each_state()
    {
        var counts = new StateCounts(1, 2, 3, 4, 5, 6);
        Assert.Equal(21, counts.Total);
        Assert.Equal(5, counts.Get(JobState.Failed));
        Assert.Equal(3, counts.Get(JobState.Processing));
    }
}
