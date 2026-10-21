using MassTransit;
using Turno.Core.Handlers;
using Turno.Core.Options;
using Turno.Infrastructure.Jobs;
using Turno.Infrastructure.Messaging;
using Turno.Worker.Execution;
using Turno.Worker.Services;

namespace Turno.Worker;

public static class WorkerServiceCollectionExtensions
{
    public static IServiceCollection AddTurnoWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var worker = configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions();
        var queues = worker.EffectiveQueues();

        JobHandlerServiceCollectionExtensions.GetRegistry(services);
        services.AddSingleton<WorkerIdentity>();
        services.AddSingleton<RunningJobs>();
        services.AddSingleton<JobRunner>();

        services.AddHostedService<WorkerRegistrationService>();
        services.AddHostedService<RecurringJobSeeder>();
        services.AddHostedService<HeartbeatService>();
        services.AddHostedService<SchedulerService>();
        services.AddHostedService<RecoveryService>();
        services.AddHostedService<RetentionService>();

        services.Configure<HostOptions>(host =>
        {
            host.ShutdownTimeout = worker.ShutdownTimeout + TimeSpan.FromSeconds(10);
        });

        services.AddTurnoMessaging(
            configuration,
            (bus, instance) =>
            {
                bus.AddConsumer<ExecuteJobConsumer>().ExcludeFromConfigureEndpoints();
                bus.AddConsumer<JobCancellationConsumer>().Endpoint(endpoint => instance.Temporary(endpoint, "cancelamentos"));
            },
            (context, cfg, messaging) =>
            {
                foreach (var queue in queues)
                {
                    var concurrency = Math.Max(1, queue.Concurrency);
                    cfg.ReceiveEndpoint(QueueAddress.EndpointName(messaging.QueuePrefix, queue.Name), endpoint =>
                    {
                        endpoint.PrefetchCount = Math.Max(concurrency, queue.PrefetchCount ?? concurrency);
                        endpoint.ConcurrentMessageLimit = concurrency;
                        if (endpoint is IServiceBusReceiveEndpointConfigurator serviceBus)
                        {
                            serviceBus.MaxAutoRenewDuration = TimeSpan.FromHours(12);
                        }

                        endpoint.ConfigureConsumer<ExecuteJobConsumer>(context);
                    });
                }
            });

        return services;
    }
}
