using System.Text;
using System.Text.Json;
using Turno.Contracts.Api;
using Turno.Core.Domain;
using Turno.Core.Options;
using Turno.Core.Scheduling;

namespace Turno.Infrastructure.Jobs;

public static class JobRequestMapper
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromDays(365);

    public static JobSubmission? TryMap(
        EnqueueJobRequest request,
        string? headerIdempotencyKey,
        ResilienceOptions resilience,
        out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resilience);
        errors = [];

        var type = JobTypeNames.Normalize(request.Type);
        if (!JobTypeNames.IsValid(type))
        {
            errors["type"] = ["Informe o tipo da tarefa com letras minúsculas, números, ponto, hífen ou sublinhado (até 200 caracteres)."];
        }

        var queue = QueueNames.Normalize(request.Queue);
        if (!QueueNames.IsValid(queue))
        {
            errors["queue"] = ["Nome de fila inválido. Use letras minúsculas, números e hífen (até 63 caracteres)."];
        }

        var payload = SerializePayload(request.Payload, errors, "payload");

        DateTimeOffset? runAt = null;
        if (request.RunAt is not null && request.DelaySeconds is not null)
        {
            errors["runAt"] = ["Use runAt ou delaySeconds, não os dois."];
        }
        else if (request.DelaySeconds is { } delay)
        {
            if (delay < 0 || TimeSpan.FromSeconds(delay) > MaxDelay)
            {
                errors["delaySeconds"] = ["O atraso deve estar entre 0 e 31536000 segundos."];
            }
            else if (delay > 0)
            {
                runAt = DateTimeOffset.UtcNow.AddSeconds(delay);
            }
        }
        else if (request.RunAt is { } at)
        {
            runAt = at.ToUniversalTime();
        }

        var maxAttempts = request.MaxAttempts ?? resilience.MaxAttemptsFor(type);
        if (maxAttempts is < 1 or > Limits.MaxAttempts)
        {
            errors["maxAttempts"] = [$"O número máximo de tentativas deve estar entre 1 e {Limits.MaxAttempts}."];
        }

        if (request.TimeoutSeconds is { } timeout && (timeout < 1 || timeout > Limits.MaxTimeoutSeconds))
        {
            errors["timeoutSeconds"] = [$"O tempo limite deve estar entre 1 e {Limits.MaxTimeoutSeconds} segundos."];
        }

        var idempotencyKey = Clean(request.IdempotencyKey ?? headerIdempotencyKey);
        if (idempotencyKey is { Length: > Limits.MaxKeyLength })
        {
            errors["idempotencyKey"] = [$"A chave de idempotência pode ter até {Limits.MaxKeyLength} caracteres."];
        }

        var concurrencyKey = Clean(request.ConcurrencyKey);
        if (concurrencyKey is { Length: > Limits.MaxKeyLength })
        {
            errors["concurrencyKey"] = [$"A chave de concorrência pode ter até {Limits.MaxKeyLength} caracteres."];
        }

        if (errors.Count > 0)
        {
            return null;
        }

        return new JobSubmission(type, queue, payload, runAt, maxAttempts, request.TimeoutSeconds, idempotencyKey, concurrencyKey, null, "Criada pela API.");
    }

    public static RecurringDefinition? TryMap(UpsertRecurringJobRequest request, ResilienceOptions resilience, out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resilience);
        errors = [];

        var name = JobTypeNames.Normalize(request.Name);
        if (!JobTypeNames.IsValid(name))
        {
            errors["name"] = ["Informe um nome com letras minúsculas, números, ponto, hífen ou sublinhado."];
        }

        var type = JobTypeNames.Normalize(request.Type);
        if (!JobTypeNames.IsValid(type))
        {
            errors["type"] = ["Tipo de tarefa inválido."];
        }

        var queue = QueueNames.Normalize(request.Queue);
        if (!QueueNames.IsValid(queue))
        {
            errors["queue"] = ["Nome de fila inválido."];
        }

        var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone.Trim();
        if (!CronSchedule.TryParse(request.Cron, timeZone, out var schedule, out var cronError))
        {
            errors["cron"] = [cronError];
        }

        var payload = SerializePayload(request.Payload, errors, "payload");
        var maxAttempts = request.MaxAttempts ?? resilience.MaxAttemptsFor(type);
        if (maxAttempts is < 1 or > Limits.MaxAttempts)
        {
            errors["maxAttempts"] = [$"O número máximo de tentativas deve estar entre 1 e {Limits.MaxAttempts}."];
        }

        if (request.TimeoutSeconds is { } timeout && (timeout < 1 || timeout > Limits.MaxTimeoutSeconds))
        {
            errors["timeoutSeconds"] = [$"O tempo limite deve estar entre 1 e {Limits.MaxTimeoutSeconds} segundos."];
        }

        if (errors.Count > 0 || schedule is null)
        {
            return null;
        }

        return new RecurringDefinition(name, type, queue, schedule.Expression, timeZone, payload, request.Enabled, request.AllowConcurrent, maxAttempts, request.TimeoutSeconds);
    }

    private static string? SerializePayload(JsonElement? payload, Dictionary<string, string[]> errors, string field)
    {
        if (payload is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var raw = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > Limits.MaxPayloadBytes)
        {
            errors[field] = [$"O payload pode ter até {Limits.MaxPayloadBytes / 1024} KB."];
            return null;
        }

        return raw;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
