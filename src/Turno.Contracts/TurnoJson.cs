using System.Text.Json;
using System.Text.Json.Serialization;

namespace Turno.Contracts;

public static class TurnoJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }

    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        if (!options.Converters.OfType<JsonStringEnumConverter>().Any())
        {
            options.Converters.Add(new JsonStringEnumConverter());
        }
    }
}

public static class TurnoHeaders
{
    public const string ApiKey = "X-Turno-Key";
    public const string IdempotencyKey = "Idempotency-Key";
    public const string AccessTokenQuery = "access_token";
}

public static class HubMethods
{
    public const string JobsChanged = "JobsChanged";
    public const string WorkersChanged = "WorkersChanged";
}
