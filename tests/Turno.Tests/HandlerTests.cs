using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Turno.Core.Handlers;
using Turno.Samples;

namespace Turno.Tests;

public class JobHandlerRegistryTests
{
    [JobType("test.echo")]
    private sealed class EchoHandler : IJobHandler
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OtherHandler : IJobHandler
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void Registers_handler_by_attribute()
    {
        var services = new ServiceCollection();
        services.AddJobHandler<EchoHandler>();
        var registry = JobHandlerServiceCollectionExtensions.GetRegistry(services);

        Assert.True(registry.TryGet("TEST.ECHO", out var type));
        Assert.Equal(typeof(EchoHandler), type);
        Assert.Contains(services, d => d.ServiceType == typeof(EchoHandler));
    }

    [Fact]
    public void Rejects_conflicting_registration()
    {
        var services = new ServiceCollection();
        services.AddJobHandler<EchoHandler>();
        Assert.Throws<InvalidOperationException>(() => services.AddJobHandler<OtherHandler>("test.echo"));
    }

    [Fact]
    public void Requires_a_type_name()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddJobHandler<OtherHandler>());
        Assert.Throws<ArgumentException>(() => services.AddJobHandler<OtherHandler>("Tipo Inválido"));
    }

    [Fact]
    public void Discovers_sample_handlers_from_assembly()
    {
        var services = new ServiceCollection();
        services.AddJobHandlersFromAssembly(typeof(SampleHandlers).Assembly);
        var registry = JobHandlerServiceCollectionExtensions.GetRegistry(services);

        Assert.Equal(
            [SampleHandlers.ProcessFile, SampleHandlers.SyncIntegration, SampleHandlers.SendNotification, SampleHandlers.GenerateReport],
            registry.Types);
        Assert.Equal(4, registry.Count);
    }
}

public class JobContextTests
{
    private sealed class NoProgress : IJobProgressSink
    {
        public List<int> Reported { get; } = [];

        public ValueTask ReportAsync(int percent, CancellationToken cancellationToken)
        {
            Reported.Add(percent);
            return ValueTask.CompletedTask;
        }
    }

    private static JobContext Create(string? payload, NoProgress? sink = null) =>
        new(Guid.NewGuid(), "report.generate", "default", 2, 3, payload, "worker-1", DateTimeOffset.UtcNow, sink ?? new NoProgress(), NullLogger.Instance);

    [Fact]
    public void Deserializes_camel_case_payload()
    {
        var payload = Create("""{"rows": 1200, "segments": 3}""").GetPayload<GenerateReportPayload>();
        Assert.NotNull(payload);
        Assert.Equal(1200, payload.Rows);
        Assert.Equal(3, payload.Segments);
    }

    [Fact]
    public void Invalid_payload_is_a_permanent_error()
    {
        var context = Create("{ isso não é json");
        Assert.Throws<PermanentJobException>(() => context.GetPayload<GenerateReportPayload>());
    }

    [Fact]
    public async Task Clamps_progress_values()
    {
        var sink = new NoProgress();
        var context = Create(null, sink);
        await context.ReportProgressAsync(150);
        await context.ReportProgressAsync(-3);
        Assert.Equal([100, 0], sink.Reported);
    }

    [Fact]
    public async Task Typed_handler_requires_payload()
    {
        var handler = new GenerateReportHandler();
        await Assert.ThrowsAsync<PermanentJobException>(() => handler.ExecuteAsync(Create(null), CancellationToken.None));
    }

    [Fact]
    public async Task Report_sample_produces_result_and_progress()
    {
        var sink = new NoProgress();
        var context = Create("""{"rows": 12000, "segments": 4, "seed": 7}""", sink);
        await new GenerateReportHandler().ExecuteAsync(context, CancellationToken.None);

        Assert.NotNull(context.Result);
        Assert.Equal(100, sink.Reported[^1]);
        Assert.True(context.IsLastAttempt is false);
    }

    [Fact]
    public async Task Notification_sample_rejects_unknown_channel()
    {
        var context = Create("""{"to": "pessoa@example.com", "channel": "pombo"}""");
        await Assert.ThrowsAsync<PermanentJobException>(() => new SendNotificationHandler().ExecuteAsync(context, CancellationToken.None));
    }
}
