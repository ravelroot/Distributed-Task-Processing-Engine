namespace Turno.Core.Scheduling;

public static class RetryBackoff
{
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, double jitter, double randomSample)
    {
        if (baseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var raw = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var ceiling = Math.Max(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds);
        var capped = Math.Min(raw, ceiling);
        var spread = Math.Clamp(jitter, 0, 1);
        var sample = Math.Clamp(randomSample, 0, 1);
        var factor = 1 + spread * (2 * sample - 1);
        var value = Math.Clamp(capped * factor, 0, ceiling);
        return TimeSpan.FromMilliseconds(Math.Round(value));
    }

    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, double jitter) =>
        Compute(attempt, baseDelay, maxDelay, jitter, Random.Shared.NextDouble());
}
