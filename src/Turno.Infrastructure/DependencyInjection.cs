using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Turno.Core.Locking;
using Turno.Core.Options;
using Turno.Infrastructure.Jobs;
using Turno.Infrastructure.Locking;
using Turno.Infrastructure.Options;
using Turno.Infrastructure.Persistence;
using Turno.Infrastructure.Resilience;

namespace Turno.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddTurnoInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<WorkerOptions>().Bind(configuration.GetSection(WorkerOptions.SectionName));
        services.AddOptions<SchedulerOptions>().Bind(configuration.GetSection(SchedulerOptions.SectionName));
        services.AddOptions<RecoveryOptions>().Bind(configuration.GetSection(RecoveryOptions.SectionName));
        services.AddOptions<RetentionOptions>().Bind(configuration.GetSection(RetentionOptions.SectionName));
        services.AddOptions<ResilienceOptions>().Bind(configuration.GetSection(ResilienceOptions.SectionName));
        services.AddOptions<MessagingOptions>().Bind(configuration.GetSection(MessagingOptions.SectionName));
        services.AddOptions<RedisOptions>().Bind(configuration.GetSection(RedisOptions.SectionName));
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));

        services.AddDbContext<TurnoDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString(DatabaseOptions.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"Configure a conexão ConnectionStrings:{DatabaseOptions.ConnectionStringName} com o PostgreSQL.");
            }

            TurnoDatabase.Configure(options, connectionString);
        });

        services.AddSingleton<RedisConnections>();
        services.AddSingleton<IDistributedLockProvider, RedLockDistributedLockProvider>();
        services.AddSingleton<JobResiliencePipelines>();
        services.AddScoped<JobStore>();
        services.AddScoped<JobQueries>();
        services.AddScoped<WorkerStore>();
        services.AddHostedService<DatabaseMigrationService>();

        services.AddHealthChecks()
            .AddDbContextCheck<TurnoDbContext>("postgres", HealthStatus.Unhealthy, [ReadyTag])
            .AddCheck<RedisHealthCheck>("redis", HealthStatus.Unhealthy, [ReadyTag]);

        return services;
    }
}
