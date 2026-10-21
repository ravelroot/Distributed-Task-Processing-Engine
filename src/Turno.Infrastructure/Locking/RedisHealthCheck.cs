using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Turno.Infrastructure.Locking;

public sealed class RedisHealthCheck(RedisConnections connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var total = connections.Multiplexers.Count;
        var healthy = 0;
        foreach (var multiplexer in connections.Multiplexers)
        {
            try
            {
                await multiplexer.GetDatabase().PingAsync();
                healthy++;
            }
            catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException)
            {
                continue;
            }
        }

        var quorum = total / 2 + 1;
        var description = $"{healthy} de {total} instância(s) Redis respondendo (quórum {quorum}).";
        if (healthy == total)
        {
            return HealthCheckResult.Healthy(description);
        }

        return healthy >= quorum ? HealthCheckResult.Degraded(description) : HealthCheckResult.Unhealthy(description);
    }
}
