using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Turno.Core.Handlers;

namespace Turno.Samples;

public static class SampleHandlers
{
    public const string GenerateReport = "report.generate";
    public const string ProcessFile = "file.process";
    public const string SyncIntegration = "integration.sync";
    public const string SendNotification = "notification.send";
}

public sealed record GenerateReportPayload(int Rows = 100_000, int Segments = 8, int? Seed = null);

[JobType(SampleHandlers.GenerateReport)]
public sealed class GenerateReportHandler : JobHandler<GenerateReportPayload>
{
    private const int Chunk = 5_000;

    protected override async Task ExecuteAsync(GenerateReportPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        if (payload.Rows is < 1 or > 50_000_000)
        {
            throw new PermanentJobException("rows deve estar entre 1 e 50.000.000.");
        }

        var segments = Math.Clamp(payload.Segments, 1, 64);
        var random = payload.Seed is { } seed ? new Random(seed) : new Random();
        var totals = new decimal[segments];
        var counts = new long[segments];
        var watch = Stopwatch.StartNew();

        for (var processed = 0; processed < payload.Rows;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(Chunk, payload.Rows - processed);
            for (var i = 0; i < take; i++)
            {
                var segment = random.Next(segments);
                totals[segment] += Math.Round((decimal)(random.NextDouble() * 1_000), 2);
                counts[segment]++;
            }

            processed += take;
            await context.ReportProgressAsync((int)(processed * 100L / payload.Rows), cancellationToken);
            await Task.Yield();
        }

        var grandTotal = totals.Sum();
        context.SetResult(new
        {
            rows = payload.Rows,
            segments = Enumerable.Range(0, segments).Select(i => new
            {
                segment = i + 1,
                rows = counts[i],
                total = totals[i],
                average = counts[i] == 0 ? 0 : Math.Round(totals[i] / counts[i], 2)
            }),
            total = grandTotal,
            elapsedMs = watch.ElapsedMilliseconds
        });
        context.Logger.LogInformation("Relatório com {Rows} linhas gerado em {Elapsed} ms.", payload.Rows, watch.ElapsedMilliseconds);
    }
}

public sealed record ProcessFilePayload(int SizeMb = 64, int ChunkMb = 4, int DelayPerChunkMs = 150);

[JobType(SampleHandlers.ProcessFile)]
public sealed class ProcessFileHandler : JobHandler<ProcessFilePayload>
{
    protected override async Task ExecuteAsync(ProcessFilePayload payload, JobContext context, CancellationToken cancellationToken)
    {
        if (payload.SizeMb is < 1 or > 10_240 || payload.ChunkMb is < 1 or > 256)
        {
            throw new PermanentJobException("sizeMb deve estar entre 1 e 10240 e chunkMb entre 1 e 256.");
        }

        var chunkBytes = payload.ChunkMb * 1024 * 1024;
        var chunks = (int)Math.Ceiling(payload.SizeMb / (double)payload.ChunkMb);
        var buffer = new byte[chunkBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (var i = 0; i < chunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RandomNumberGenerator.Fill(buffer);
            hash.AppendData(buffer);
            if (payload.DelayPerChunkMs > 0)
            {
                await Task.Delay(payload.DelayPerChunkMs, cancellationToken);
            }

            await context.ReportProgressAsync((i + 1) * 100 / chunks, cancellationToken);
        }

        context.SetResult(new
        {
            chunks,
            sizeMb = payload.SizeMb,
            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset())
        });
    }
}

public sealed record SyncIntegrationPayload(int Records = 50, double FailureRate = 0.3, int LatencyMs = 400);

[JobType(SampleHandlers.SyncIntegration)]
public sealed class SyncIntegrationHandler : JobHandler<SyncIntegrationPayload>
{
    protected override async Task ExecuteAsync(SyncIntegrationPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        var rate = Math.Clamp(payload.FailureRate, 0, 1);
        var latency = Math.Clamp(payload.LatencyMs, 0, 60_000);
        await Task.Delay(Random.Shared.Next(latency / 2, latency + 1), cancellationToken);

        if (Random.Shared.NextDouble() < rate)
        {
            throw new HttpRequestException("Serviço parceiro respondeu 503 (simulação).", null, HttpStatusCode.ServiceUnavailable);
        }

        var records = Math.Clamp(payload.Records, 0, 1_000_000);
        await context.ReportProgressAsync(100, cancellationToken);
        context.SetResult(new { records, attempt = context.Attempt });
    }
}

public sealed record SendNotificationPayload(string? To, string Channel = "email", string? Subject = null, string? Message = null);

[JobType(SampleHandlers.SendNotification)]
public sealed class SendNotificationHandler : JobHandler<SendNotificationPayload>
{
    private static readonly string[] Channels = ["email", "sms", "push"];

    protected override async Task ExecuteAsync(SendNotificationPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.To))
        {
            throw new PermanentJobException("Informe o destinatário em 'to'.");
        }

        if (!Channels.Contains(payload.Channel, StringComparer.OrdinalIgnoreCase))
        {
            throw new PermanentJobException($"Canal '{payload.Channel}' não suportado. Use email, sms ou push.");
        }

        await Task.Delay(Random.Shared.Next(80, 250), cancellationToken);
        context.SetResult(new { channel = payload.Channel.ToLowerInvariant(), to = Mask(payload.To), deliveredAt = DateTimeOffset.UtcNow });
    }

    private static string Mask(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 4 ? "****" : $"{trimmed[..2]}***{trimmed[^2..]}";
    }
}
