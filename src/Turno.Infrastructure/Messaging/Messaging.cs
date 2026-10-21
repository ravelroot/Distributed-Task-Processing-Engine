using System.Data;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Turno.Contracts.Messages;
using Turno.Infrastructure.Options;
using Turno.Infrastructure.Persistence;

namespace Turno.Infrastructure.Messaging;

public static class QueueAddress
{
    public static string EndpointName(string prefix, string queue) => $"{prefix}-{queue}";

    public static Uri For(string prefix, string queue) => new($"queue:{EndpointName(prefix, queue)}");

    public static string DeadLetterName(string prefix) => $"{prefix}-dead-letter";

    public static Uri DeadLetter(string prefix) => new($"queue:{DeadLetterName(prefix)}");
}

public sealed class BusInstance(string prefix)
{
    public string Prefix { get; } = prefix;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    public void Temporary(IEndpointRegistrationConfigurator endpoint, string name)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Name = $"{Prefix}-{name}-{Id}";
        endpoint.Temporary = true;
    }
}

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddTurnoMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator, BusInstance>? registerConsumers = null,
        Action<IBusRegistrationContext, IBusFactoryConfigurator, MessagingOptions>? configureEndpoints = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>() ?? new MessagingOptions();
        var instance = new BusInstance(settings.QueuePrefix);

        services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();
            bus.DisableUsageTelemetry();
            registerConsumers?.Invoke(bus, instance);

            bus.AddEntityFrameworkOutbox<TurnoDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.IsolationLevel = IsolationLevel.ReadCommitted;
                outbox.QueryDelay = settings.OutboxQueryDelay;
                outbox.DisableInboxCleanupService();
            });

            switch (settings.Transport)
            {
                case MessagingTransport.AzureServiceBus:
                    bus.UsingAzureServiceBus((context, cfg) =>
                    {
                        if (string.IsNullOrWhiteSpace(settings.AzureServiceBus.ConnectionString))
                        {
                            throw new InvalidOperationException("Configure Turno:Messaging:AzureServiceBus:ConnectionString.");
                        }

                        cfg.Host(settings.AzureServiceBus.ConnectionString);
                        ConfigureCommon(context, cfg, settings);
                        configureEndpoints?.Invoke(context, cfg, settings);
                    });
                    break;

                default:
                    bus.UsingRabbitMq((context, cfg) =>
                    {
                        var rabbit = settings.RabbitMq;
                        cfg.Host(rabbit.Host, rabbit.Port, rabbit.VirtualHost, host =>
                        {
                            if (!string.IsNullOrEmpty(rabbit.Username))
                            {
                                host.Username(rabbit.Username);
                                host.Password(rabbit.Password);
                            }

                            if (rabbit.UseSsl)
                            {
                                host.UseSsl(_ => { });
                            }
                        });
                        ConfigureCommon(context, cfg, settings);
                        configureEndpoints?.Invoke(context, cfg, settings);
                    });
                    break;
            }
        });

        return services;
    }

    private static void ConfigureCommon<TEndpoint>(IBusRegistrationContext context, IBusFactoryConfigurator<TEndpoint> cfg, MessagingOptions settings)
        where TEndpoint : IReceiveEndpointConfigurator
    {
        var prefix = settings.QueuePrefix;
        cfg.Message<JobStatusChanged>(m => m.SetEntityName($"{prefix}.job-status-changed"));
        cfg.Message<WorkerStatusChanged>(m => m.SetEntityName($"{prefix}.worker-status-changed"));
        cfg.Message<JobCancellationRequested>(m => m.SetEntityName($"{prefix}.job-cancellation-requested"));
        cfg.Message<ExecuteJob>(m => m.SetEntityName($"{prefix}.execute-job"));
        cfg.Message<JobDeadLettered>(m => m.SetEntityName($"{prefix}.job-dead-lettered"));
        cfg.UseMessageRetry(retry => retry.Incremental(Math.Max(1, settings.ConsumerRetryLimit), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
        cfg.ConfigureEndpoints(context);
    }
}
