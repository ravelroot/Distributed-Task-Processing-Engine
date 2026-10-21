namespace Turno.Infrastructure.Options;

public enum MessagingTransport
{
    RabbitMq = 0,
    AzureServiceBus = 1
}

public sealed class MessagingOptions
{
    public const string SectionName = "Turno:Messaging";

    public MessagingTransport Transport { get; set; } = MessagingTransport.RabbitMq;
    public string QueuePrefix { get; set; } = "turno";
    public TimeSpan OutboxQueryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public int ConsumerRetryLimit { get; set; } = 10;
    public RabbitMqOptions RabbitMq { get; set; } = new();
    public AzureServiceBusOptions AzureServiceBus { get; set; } = new();
}

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public ushort Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseSsl { get; set; }
}

public sealed class AzureServiceBusOptions
{
    public string ConnectionString { get; set; } = string.Empty;
}

public sealed class RedisOptions
{
    public const string SectionName = "Turno:Redis";

    public List<string> Connections { get; set; } = [];
    public string KeyPrefix { get; set; } = "turno";
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Turno:Database";
    public const string ConnectionStringName = "Turno";
    public const string Schema = "turno";

    public bool ApplyMigrations { get; set; } = true;
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
