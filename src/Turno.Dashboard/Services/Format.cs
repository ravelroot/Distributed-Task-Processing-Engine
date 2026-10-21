using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Turno.Contracts;

namespace Turno.Dashboard.Services;

public static class Format
{
    private static readonly CultureInfo Culture = new("pt-BR");
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string State(JobState state) => state switch
    {
        JobState.Scheduled => "Agendada",
        JobState.Enqueued => "Na fila",
        JobState.Processing => "Executando",
        JobState.Succeeded => "Sucesso",
        JobState.Failed => "Falha",
        JobState.Cancelled => "Cancelada",
        _ => state.ToString()
    };

    public static string StateClass(JobState state) => state switch
    {
        JobState.Scheduled => "scheduled",
        JobState.Enqueued => "enqueued",
        JobState.Processing => "processing",
        JobState.Succeeded => "succeeded",
        JobState.Failed => "failed",
        JobState.Cancelled => "cancelled",
        _ => "enqueued"
    };

    public static string Outcome(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.Running => "Em andamento",
        AttemptOutcome.Succeeded => "Sucesso",
        AttemptOutcome.Failed => "Falhou",
        AttemptOutcome.Deferred => "Adiada",
        AttemptOutcome.Interrupted => "Interrompida",
        AttemptOutcome.Lost => "Worker perdido",
        AttemptOutcome.Cancelled => "Cancelada",
        _ => outcome.ToString()
    };

    public static string OutcomeClass(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.Running => "processing",
        AttemptOutcome.Succeeded => "succeeded",
        AttemptOutcome.Failed or AttemptOutcome.Lost => "failed",
        AttemptOutcome.Deferred or AttemptOutcome.Interrupted => "scheduled",
        _ => "cancelled"
    };

    public static string Worker(WorkerStatus status) => status switch
    {
        WorkerStatus.Online => "Online",
        WorkerStatus.Stopping => "Encerrando",
        _ => "Offline"
    };

    public static string WorkerClass(WorkerStatus status) => status switch
    {
        WorkerStatus.Online => "succeeded",
        WorkerStatus.Stopping => "scheduled",
        _ => "cancelled"
    };

    public static string ShortId(Guid id) => id.ToString("N")[^8..];

    public static string Breakable(string value) => value.Replace(".", ".\u200B", StringComparison.Ordinal);

    public static string Number(long value) => value.ToString("N0", Culture);

    public static string DateTime(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("dd/MM HH:mm:ss", Culture) : "—";

    public static string FullDateTime(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", Culture) : "—";

    public static string Relative(DateTimeOffset? value, DateTimeOffset now)
    {
        if (value is not { } v)
        {
            return "—";
        }

        var delta = now - v;
        var future = delta < TimeSpan.Zero;
        var span = future ? -delta : delta;
        string text;
        if (span.TotalSeconds < 5)
        {
            return future ? "em instantes" : "agora";
        }

        if (span.TotalMinutes < 1)
        {
            text = $"{(int)span.TotalSeconds} s";
        }
        else if (span.TotalHours < 1)
        {
            text = $"{(int)span.TotalMinutes} min";
        }
        else if (span.TotalDays < 1)
        {
            text = $"{(int)span.TotalHours} h";
        }
        else
        {
            text = $"{(int)span.TotalDays} d";
        }

        return future ? $"em {text}" : $"há {text}";
    }

    public static string Duration(double? milliseconds)
    {
        if (milliseconds is not { } ms)
        {
            return "—";
        }

        if (ms < 1000)
        {
            return $"{ms:0} ms";
        }

        var span = TimeSpan.FromMilliseconds(ms);
        if (span.TotalMinutes < 1)
        {
            return $"{span.TotalSeconds.ToString("0.0", Culture)} s";
        }

        return span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s" : $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    public static string Duration(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now) =>
        start is { } s ? Duration(((end ?? now) - s).TotalMilliseconds) : "—";

    public static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, Indented);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
