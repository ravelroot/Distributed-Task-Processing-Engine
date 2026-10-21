using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;
using Turno.Core.Locking;
using Turno.Infrastructure.Options;

namespace Turno.Infrastructure.Locking;

public sealed class RedisConnections : IAsyncDisposable
{
    private readonly List<ConnectionMultiplexer> multiplexers;

    public RedisConnections(IOptions<RedisOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connections = options.Value.Connections.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (connections.Count == 0)
        {
            throw new InvalidOperationException("Configure ao menos uma conexão em Turno:Redis:Connections.");
        }

        multiplexers = connections
            .Select(connection =>
            {
                var configuration = ConfigurationOptions.Parse(connection);
                configuration.AbortOnConnectFail = false;
                configuration.ConnectRetry = Math.Max(configuration.ConnectRetry, 5);
                return ConnectionMultiplexer.Connect(configuration);
            })
            .ToList();
    }

    public IReadOnlyList<ConnectionMultiplexer> Multiplexers => multiplexers;

    public ConnectionMultiplexer Primary => multiplexers[0];

    public async ValueTask DisposeAsync()
    {
        foreach (var multiplexer in multiplexers)
        {
            await multiplexer.DisposeAsync();
        }
    }
}

public sealed class RedLockDistributedLockProvider : IDistributedLockProvider, IDisposable
{
    private readonly RedLockFactory factory;
    private readonly string prefix;

    public RedLockDistributedLockProvider(RedisConnections connections, IOptions<RedisOptions> options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        var endpoints = connections.Multiplexers.Select(m => new RedLockMultiplexer(m)).ToList();
        factory = RedLockFactory.Create(endpoints, loggerFactory);
        prefix = string.IsNullOrWhiteSpace(options.Value.KeyPrefix) ? "turno" : options.Value.KeyPrefix.Trim();
    }

    public async Task<IDistributedLock?> TryAcquireAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var redLock = await factory.CreateLockAsync(Key(resource), expiry);
        return await WrapAsync(resource, redLock);
    }

    public async Task<IDistributedLock?> AcquireAsync(string resource, TimeSpan expiry, TimeSpan wait, TimeSpan retry, CancellationToken cancellationToken)
    {
        var redLock = await factory.CreateLockAsync(Key(resource), expiry, wait, retry, cancellationToken);
        return await WrapAsync(resource, redLock);
    }

    public void Dispose() => factory.Dispose();

    private string Key(string resource) => $"{prefix}:{resource}";

    private static async Task<IDistributedLock?> WrapAsync(string resource, IRedLock redLock)
    {
        if (redLock.IsAcquired)
        {
            return new RedLockHandle(resource, redLock);
        }

        var status = redLock.Status;
        await redLock.DisposeAsync();
        if (status == RedLockStatus.NoQuorum)
        {
            throw new DistributedLockUnavailableException($"Não foi possível obter quórum no Redis para o recurso '{resource}'.");
        }

        return null;
    }

    private sealed class RedLockHandle(string resource, IRedLock redLock) : IDistributedLock
    {
        public string Resource { get; } = resource;

        public string LockId => redLock.LockId;

        public bool IsHeld => redLock.IsAcquired;

        public ValueTask DisposeAsync() => redLock.DisposeAsync();
    }
}
