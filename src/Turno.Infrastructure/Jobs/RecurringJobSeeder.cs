using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Turno.Contracts.Api;
using Turno.Core.Options;

namespace Turno.Infrastructure.Jobs;

public sealed partial class RecurringJobSeeder(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IOptions<ResilienceOptions> resilience,
    ILogger<RecurringJobSeeder> logger) : IHostedService
{
    public const string SectionName = "Turno:RecurringJobs";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var entries = configuration.GetSection(SectionName).GetChildren().ToList();
        if (entries.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<JobStore>();
        foreach (var entry in entries)
        {
            var request = new UpsertRecurringJobRequest
            {
                Name = entry["Name"] ?? string.Empty,
                Type = entry["Type"] ?? string.Empty,
                Cron = entry["Cron"] ?? string.Empty,
                Queue = entry["Queue"],
                TimeZone = entry["TimeZone"],
                Payload = ConfigurationJson.ToElement(entry.GetSection("Payload")),
                Enabled = entry.GetValue<bool?>("Enabled") ?? true,
                AllowConcurrent = entry.GetValue<bool?>("AllowConcurrent") ?? false,
                MaxAttempts = entry.GetValue<int?>("MaxAttempts"),
                TimeoutSeconds = entry.GetValue<int?>("TimeoutSeconds")
            };

            var definition = JobRequestMapper.TryMap(request, resilience.Value, out var errors);
            if (definition is null)
            {
                LogInvalid(logger, entry.Path, string.Join("; ", errors.SelectMany(e => e.Value.Select(v => $"{e.Key}: {v}"))));
                continue;
            }

            try
            {
                var result = await store.UpsertRecurringAsync(definition, true, cancellationToken);
                LogSeeded(logger, definition.Name, result.Message);
            }
            catch (DbUpdateException ex)
            {
                LogConcurrent(logger, definition.Name, ex.InnerException?.Message ?? ex.Message);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "Agendamento recorrente inválido em {Path}: {Errors}")]
    private static partial void LogInvalid(ILogger logger, string path, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agendamento recorrente '{Name}' carregado da configuração: {Result}")]
    private static partial void LogSeeded(ILogger logger, string name, string result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agendamento '{Name}' já foi registrado por outra instância: {Error}")]
    private static partial void LogConcurrent(ILogger logger, string name, string error);
}

public static class ConfigurationJson
{
    public static JsonElement? ToElement(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!section.Exists())
        {
            return null;
        }

        var node = ToNode(section);
        return node is null ? null : JsonSerializer.SerializeToElement(node);
    }

    private static JsonNode? ToNode(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return section.Value is null ? null : Scalar(section.Value);
        }

        if (children.All(c => int.TryParse(c.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            var array = new JsonArray();
            foreach (var child in children.OrderBy(c => int.Parse(c.Key, CultureInfo.InvariantCulture)))
            {
                array.Add(ToNode(child));
            }

            return array;
        }

        var obj = new JsonObject();
        foreach (var child in children)
        {
            obj[child.Key] = ToNode(child);
        }

        return obj;
    }

    private static JsonValue Scalar(string value)
    {
        if (bool.TryParse(value, out var boolean))
        {
            return JsonValue.Create(boolean);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(value);
    }
}
