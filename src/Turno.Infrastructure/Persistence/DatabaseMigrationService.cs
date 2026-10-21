using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Turno.Infrastructure.Options;

namespace Turno.Infrastructure.Persistence;

public sealed partial class DatabaseMigrationService(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrationService> logger) : IHostedService
{
    private const long AdvisoryLockKey = 842_117_026;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.StartupTimeout);

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
                MaxRetryAttempts = int.MaxValue,
                Delay = TimeSpan.FromSeconds(2),
                MaxDelay = TimeSpan.FromSeconds(10),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    LogDatabaseUnavailable(logger, args.Outcome.Exception?.Message, args.RetryDelay);
                    return default;
                }
            })
            .Build();

        await pipeline.ExecuteAsync(async token => await PrepareAsync(settings.ApplyMigrations, token), timeout.Token);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task PrepareAsync(bool applyMigrations, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TurnoDbContext>();

        if (!applyMigrations)
        {
            if (!await HistoryTableExistsAsync(db, cancellationToken))
            {
                throw new InvalidOperationException("Aguardando outra instância criar o banco de dados.");
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                throw new InvalidOperationException($"Aguardando a aplicação de {pending.Count} migração(ões) por outra instância.");
            }

            LogDatabaseReady(logger);
            return;
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(db, $"SELECT pg_advisory_lock({AdvisoryLockKey})", cancellationToken);
            try
            {
                await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count > 0)
                {
                    LogApplyingMigrations(logger, pending.Count, string.Join(", ", pending));
                    await db.Database.MigrateAsync(cancellationToken);
                }
            }
            finally
            {
                await ExecuteAsync(db, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        LogDatabaseReady(logger);
    }

    private static async Task<bool> HistoryTableExistsAsync(TurnoDbContext db, CancellationToken cancellationToken)
    {
        var exists = await db.Database
            .SqlQuery<bool>($"SELECT to_regclass(quote_ident({DatabaseOptions.Schema}) || '.' || quote_ident({TurnoDatabase.MigrationsHistoryTable})) IS NOT NULL AS \"Value\"")
            .ToListAsync(cancellationToken);
        return exists.Count > 0 && exists[0];
    }

    private static async Task ExecuteAsync(TurnoDbContext db, string sql, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteScalarAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Banco de dados indisponível ({Error}). Nova tentativa em {Delay}.")]
    private static partial void LogDatabaseUnavailable(ILogger logger, string? error, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Aplicando {Count} migração(ões): {Migrations}.")]
    private static partial void LogApplyingMigrations(ILogger logger, int count, string migrations);

    [LoggerMessage(Level = LogLevel.Information, Message = "Banco de dados pronto.")]
    private static partial void LogDatabaseReady(ILogger logger);
}
