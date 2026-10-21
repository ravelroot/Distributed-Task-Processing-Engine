using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Turno.Core.Domain;

public static partial class QueueNames
{
    public const string Default = "default";
    public const int MaxLength = 63;

    public static string Normalize(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Default : name.Trim().ToLowerInvariant();

    public static bool IsValid([NotNullWhen(true)] string? name) =>
        !string.IsNullOrEmpty(name) && QueuePattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex QueuePattern();
}

public static partial class JobTypeNames
{
    public const int MaxLength = 200;

    public static string Normalize(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValid([NotNullWhen(true)] string? name) =>
        !string.IsNullOrEmpty(name) && TypePattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex TypePattern();
}

public static class LockKeys
{
    public static string Job(Guid jobId) => $"job:{jobId:N}";

    public static string Resource(string concurrencyKey) => $"resource:{concurrencyKey.Trim().ToLowerInvariant()}";

    public static string Leader(string role) => $"leader:{role}";
}

public static class Limits
{
    public const int MaxPayloadBytes = 256 * 1024;
    public const int MaxResultBytes = 32 * 1024;
    public const int MaxErrorLength = 4000;
    public const int MaxStackTraceLength = 16000;
    public const int MaxAttempts = 100;
    public const int MaxTimeoutSeconds = 7 * 24 * 60 * 60;
    public const int MaxKeyLength = 200;
    public const int MaxPageSize = 200;

    public static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
