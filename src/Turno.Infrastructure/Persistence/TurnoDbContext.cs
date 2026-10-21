using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Turno.Core.Domain;
using Turno.Infrastructure.Options;

namespace Turno.Infrastructure.Persistence;

public sealed class TurnoDbContext(DbContextOptions<TurnoDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<JobStateChange> JobStateChanges => Set<JobStateChange>();
    public DbSet<RecurringJob> RecurringJobs => Set<RecurringJob>();
    public DbSet<WorkerNode> Workers => Set<WorkerNode>();
    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DatabaseOptions.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TurnoDbContext).Assembly);
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        SnakeCaseNaming.Apply(modelBuilder);
    }
}

public static class TurnoDatabase
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable(MigrationsHistoryTable, DatabaseOptions.Schema);
            npgsql.MigrationsAssembly(typeof(TurnoDbContext).Assembly.GetName().Name);
        });
    }
}

public sealed class TurnoDbContextFactory : IDesignTimeDbContextFactory<TurnoDbContext>
{
    public TurnoDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("TURNO_DESIGN_CONNECTION") ?? "Host=localhost;Database=turno";
        var builder = new DbContextOptionsBuilder<TurnoDbContext>();
        TurnoDatabase.Configure(builder, connection);
        return new TurnoDbContext(builder.Options);
    }
}
