using System.Diagnostics.CodeAnalysis;
using Cronos;

namespace Turno.Core.Scheduling;

public sealed class CronSchedule
{
    private readonly CronExpression expression;

    private CronSchedule(string text, CronExpression expression, TimeZoneInfo timeZone)
    {
        Expression = text;
        this.expression = expression;
        TimeZone = timeZone;
    }

    public string Expression { get; }

    public TimeZoneInfo TimeZone { get; }

    public static bool TryParse(string? text, string? timeZoneId, [NotNullWhen(true)] out CronSchedule? schedule, [NotNullWhen(false)] out string? error)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Informe a expressão cron.";
            return false;
        }

        var normalized = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var fields = normalized.Split(' ').Length;
        var format = fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        if (fields is not (1 or 5 or 6) || !CronExpression.TryParse(normalized, format, out var parsed))
        {
            error = $"Expressão cron inválida: '{text}'. Use 5 campos (minuto hora dia mês dia-da-semana) ou 6 com segundos.";
            return false;
        }

        if (!TryResolveTimeZone(timeZoneId, out var zone))
        {
            error = $"Fuso horário desconhecido: '{timeZoneId}'.";
            return false;
        }

        schedule = new CronSchedule(normalized, parsed, zone);
        error = null;
        return true;
    }

    public static CronSchedule Parse(string text, string? timeZoneId) =>
        TryParse(text, timeZoneId, out var schedule, out var error) ? schedule : throw new FormatException(error);

    public static bool TryResolveTimeZone(string? timeZoneId, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId.Trim(), out zone);
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset from)
    {
        var next = expression.GetNextOccurrence(from.ToUniversalTime(), TimeZone);
        return next?.ToUniversalTime();
    }

    public IEnumerable<DateTimeOffset> GetOccurrences(DateTimeOffset from, int count)
    {
        var cursor = from;
        for (var i = 0; i < count; i++)
        {
            var next = GetNextOccurrence(cursor);
            if (next is null)
            {
                yield break;
            }

            yield return next.Value;
            cursor = next.Value;
        }
    }
}
