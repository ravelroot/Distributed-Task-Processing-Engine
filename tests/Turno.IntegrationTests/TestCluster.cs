using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Turno.Contracts;
using Turno.Core.Domain;
using Turno.Core.Handlers;
using Turno.Infrastructure;
using Turno.Infrastructure.Jobs;
using Turno.Infrastructure.Persistence;
using Turno.Worker;

namespace Turno.IntegrationTests;

public static class IntegrationEnvironment
{
    public static string? Postgres => Environment.GetEnvironmentVariable("TURNO_TEST_POSTGRES");

    public static string? Redis => Environment.GetEnvironmentVariable("TURNO_TEST_REDIS");

    public static string? RabbitMq => Environment.GetEnvironmentVariable("TURNO_TEST_RABBITMQ");

    public static bool Enabled =>
        !string.IsNullOrWhiteSpace(Postgres) && !string.IsNullOrWhiteSpace(Redis) && !string.IsNullOrWhiteSpace(RabbitMq);
}

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (!IntegrationEnvironment.Enabled)
        {
            Skip = "Defina TURNO_TEST_POSTGRES, TURNO_TEST_REDIS e TURNO_TEST_RABBITMQ para rodar os testes de integração.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClusterCollection
{
    public const string Name = "cluster";
}

public sealed record Execution(Guid JobId, string WorkerId, int Attempt, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

public sealed class TestProbe
{
    public ConcurrentQueue<Execution> Executions { get; } = new();

    public ConcurrentDictionary<string, int> Counters { get; } = new();

    public string? ZombieWorker { get; set; }

    public int Increment(string key) => Counters.AddOrUpdate(key, 1, (_, value) => value + 1);
}

public sealed class TestCluster : IAsyncDisposable
{
    private readonly string adminConnection;
    private readonly string database;
    private readonly List<IHost> hosts = [];
    private readonly Dictionary<string, string?> baseSettings;

    private TestCluster(string adminConnection, string database, Dictionary<string, string?> baseSettings)
    {
        this.adminConnection = adminConnection;
        this.database = database;
        this.baseSettings = baseSettings;
    }

    public TestProbe Probe { get; } = new();

    public IReadOnlyList<IHost> Workers => hosts;

    public string ConnectionString => baseSettings["ConnectionStrings:Turno"]!;

    public static async Task<TestCluster> CreateAsync(IDictionary<string, string?>? overrides = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"turno_it_{suffix}";
        var admin = IntegrationEnvironment.Postgres!;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{database}\"";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(admin) { Database = database, Pooling = true, MaxPoolSize = 40 };
        var rabbit = new Uri(IntegrationEnvironment.RabbitMq!);
        var credentials = rabbit.UserInfo.Split(':', 2);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Turno"] = builder.ConnectionString,
            ["Turno:Messaging:QueuePrefix"] = $"it-{suffix}",
            ["Turno:Messaging:OutboxQueryDelay"] = "00:00:00.200",
            ["Turno:Messaging:RabbitMq:Host"] = rabbit.Host,
            ["Turno:Messaging:RabbitMq:Port"] = (rabbit.IsDefaultPort ? 5672 : rabbit.Port).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Turno:Messaging:RabbitMq:Username"] = Uri.UnescapeDataString(credentials[0]),
            ["Turno:Messaging:RabbitMq:Password"] = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
            ["Turno:Redis:Connections:0"] = IntegrationEnvironment.Redis,
            ["Turno:Redis:KeyPrefix"] = $"it-{suffix}",
            ["Turno:Worker:Queues:0:Name"] = "default",
            ["Turno:Worker:Queues:0:Concurrency"] = "4",
            ["Turno:Worker:LeaseDuration"] = "00:00:04",
            ["Turno:Worker:LeaseRenewalInterval"] = "00:00:01",
            ["Turno:Worker:LockExpiry"] = "00:00:05",
            ["Turno:Worker:HeartbeatInterval"] = "00:00:01",
            ["Turno:Worker:ConcurrencyKeyRetryDelay"] = "00:00:00.500",
            ["Turno:Worker:ProgressThrottle"] = "00:00:00.200",
            ["Turno:Worker:ShutdownTimeout"] = "00:00:07",
            ["Turno:Scheduler:Interval"] = "00:00:00.300",
            ["Turno:Recovery:Interval"] = "00:00:00.500",
            ["Turno:Recovery:LeaseGracePeriod"] = "00:00:01",
            ["Turno:Retention:Enabled"] = "false",
            ["Turno:Resilience:ImmediateRetries"] = "0",
            ["Turno:Resilience:BackoffBase"] = "00:00:00.500",
            ["Turno:Resilience:BackoffMax"] = "00:00:01",
            ["Turno:Resilience:BackoffJitter"] = "0",
            ["Turno:Resilience:CircuitBreaker:Enabled"] = "false",
            ["Turno:Samples:Enabled"] = "false"
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }
        }

        return new TestCluster(admin, database, settings);
    }

    public async Task<IHost> StartWorkerAsync(string name, IDictionary<string, string?>? overrides = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing", DisableDefaults = true });
        var settings = new Dictionary<string, string?>(baseSettings) { ["Turno:Worker:Name"] = name };
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddTurnoInfrastructure(builder.Configuration);
        builder.Services.AddSingleton(Probe);
        builder.Services.AddJobHandler<RecordHandler>();
        builder.Services.AddJobHandler<FailingHandler>();
        builder.Services.AddJobHandler<PermanentHandler>();
        builder.Services.AddJobHandler<SlowHandler>();
        builder.Services.AddJobHandler<ZombieHandler>();
        builder.Services.AddJobHandler<ExclusiveHandler>();
        builder.Services.AddTurnoWorker(builder.Configuration);

        var host = builder.Build();
        await host.StartAsync();
        hosts.Add(host);
        return host;
    }

    public static string WorkerId(IHost host) => host.Services.GetRequiredService<WorkerIdentity>().Id;

    public async Task<Guid> SubmitAsync(string type, string? payload = null, DateTimeOffset? runAt = null, int maxAttempts = 3, string? concurrencyKey = null)
    {
        await using var scope = hosts[0].Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<JobStore>();
        var result = await store.SubmitAsync(new JobSubmission(type, QueueNames.Default, payload, runAt, maxAttempts, null, null, concurrencyKey, null, "Teste de integração."), CancellationToken.None);
        return result.Id;
    }

    public async Task<T> QueryAsync<T>(Func<TurnoDbContext, Task<T>> query)
    {
        var options = new DbContextOptionsBuilder<TurnoDbContext>();
        TurnoDatabase.Configure(options, ConnectionString);
        await using var db = new TurnoDbContext(options.Options);
        return await query(db);
    }

    public async Task<T> WithStoreAsync<T>(Func<JobStore, Task<T>> action)
    {
        await using var scope = hosts[0].Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JobStore>());
    }

    public Task<JobState> StateAsync(Guid jobId) =>
        QueryAsync(db => db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.State).FirstAsync());

    public async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Tempo esgotado esperando: {description}");
    }

    public Task WaitForStatesAsync(IReadOnlyCollection<Guid> ids, JobState state, TimeSpan timeout) =>
        WaitForAsync(
            async () => await QueryAsync(db => db.Jobs.AsNoTracking().CountAsync(j => ids.Contains(j.Id) && j.State == state)) == ids.Count,
            timeout,
            $"{ids.Count} tarefa(s) em {state}");

    public async ValueTask DisposeAsync()
    {
        foreach (var host in hosts)
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await host.StopAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                host.Dispose();
            }
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}

[JobType("test.record")]
public sealed class RecordHandler(TestProbe probe) : IJobHandler
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        await Task.Delay(Random.Shared.Next(40, 160), cancellationToken);
        await context.ReportProgressAsync(100, cancellationToken);
        probe.Executions.Enqueue(new Execution(context.JobId, context.WorkerId, context.Attempt, started, DateTimeOffset.UtcNow));
        context.SetResult(new { worker = context.WorkerId });
    }
}

[JobType("test.fail")]
public sealed class FailingHandler(TestProbe probe) : IJobHandler
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        probe.Increment($"fail:{context.JobId}");
        throw new InvalidOperationException($"Falha simulada na tentativa {context.Attempt}.");
    }
}

[JobType("test.permanent")]
public sealed class PermanentHandler(TestProbe probe) : IJobHandler
{
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        probe.Increment($"permanent:{context.JobId}");
        throw new PermanentJobException("Dados inválidos: não adianta tentar de novo.");
    }
}

[JobType("test.slow")]
public sealed class SlowHandler(TestProbe probe) : IJobHandler
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        probe.Increment($"slow-start:{context.JobId}");
        var seconds = context.GetPayload<SlowPayload>()?.Seconds ?? 30;
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        probe.Executions.Enqueue(new Execution(context.JobId, context.WorkerId, context.Attempt, context.StartedAt, DateTimeOffset.UtcNow));
    }
}

public sealed record SlowPayload(int Seconds);

[JobType("test.zombie")]
public sealed class ZombieHandler(TestProbe probe) : IJobHandler
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        if (context.WorkerId == probe.ZombieWorker)
        {
            probe.Increment("zombie-started");
            await Task.Delay(TimeSpan.FromSeconds(9), CancellationToken.None);
            probe.Increment("zombie-finished");
            return;
        }

        probe.Executions.Enqueue(new Execution(context.JobId, context.WorkerId, context.Attempt, context.StartedAt, DateTimeOffset.UtcNow));
    }
}

[JobType("test.exclusive")]
public sealed class ExclusiveHandler(TestProbe probe) : IJobHandler
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        await Task.Delay(300, cancellationToken);
        probe.Executions.Enqueue(new Execution(context.JobId, context.WorkerId, context.Attempt, started, DateTimeOffset.UtcNow));
    }
}
