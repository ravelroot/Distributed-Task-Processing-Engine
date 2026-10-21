using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Turno.Core.Domain;

namespace Turno.Infrastructure.Persistence;

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs", table =>
        {
            table.HasCheckConstraint("ck_jobs_attempt", "attempt >= 0");
            table.HasCheckConstraint("ck_jobs_max_attempts", "max_attempts BETWEEN 1 AND 100");
            table.HasCheckConstraint("ck_jobs_progress", "progress IS NULL OR progress BETWEEN 0 AND 100");
            table.HasCheckConstraint("ck_jobs_timeout", "timeout_seconds IS NULL OR timeout_seconds > 0");
        });
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.Type).HasMaxLength(JobTypeNames.MaxLength).IsRequired();
        builder.Property(j => j.Queue).HasMaxLength(QueueNames.MaxLength).IsRequired();
        builder.Property(j => j.Payload).HasColumnType("jsonb");
        builder.Property(j => j.Result).HasColumnType("jsonb");
        builder.Property(j => j.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(j => j.WorkerId).HasMaxLength(200);
        builder.Property(j => j.IdempotencyKey).HasMaxLength(Limits.MaxKeyLength);
        builder.Property(j => j.ConcurrencyKey).HasMaxLength(Limits.MaxKeyLength);
        builder.Property(j => j.LastError).HasMaxLength(Limits.MaxErrorLength);

        builder.HasIndex(j => j.IdempotencyKey).IsUnique();
        builder.HasIndex(j => new { j.State, j.ScheduledAt });
        builder.HasIndex(j => new { j.State, j.LeaseExpiresAt });
        builder.HasIndex(j => new { j.Queue, j.State });
        builder.HasIndex(j => new { j.Type, j.CreatedAt });
        builder.HasIndex(j => j.CreatedAt);
        builder.HasIndex(j => new { j.State, j.FinishedAt });
        builder.HasIndex(j => j.RecurringJobId);
        builder.HasIndex(j => j.WorkerId);

        builder.HasOne(j => j.RecurringJob)
            .WithMany(r => r.Jobs)
            .HasForeignKey(j => j.RecurringJobId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(j => j.Worker)
            .WithMany(w => w.Jobs)
            .HasForeignKey(j => j.WorkerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(j => j.Attempts)
            .WithOne(a => a.Job)
            .HasForeignKey(a => a.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(j => j.History)
            .WithOne(h => h.Job)
            .HasForeignKey(h => h.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(j => j.DeadLetters)
            .WithOne(d => d.Job)
            .HasForeignKey(d => d.JobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JobAttemptConfiguration : IEntityTypeConfiguration<JobAttempt>
{
    public void Configure(EntityTypeBuilder<JobAttempt> builder)
    {
        builder.ToTable("job_attempts", table => table.HasCheckConstraint("ck_job_attempts_number", "number >= 1"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityAlwaysColumn();
        builder.Property(a => a.WorkerId).HasMaxLength(200);
        builder.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.ErrorType).HasMaxLength(300);
        builder.Property(a => a.ErrorMessage).HasMaxLength(Limits.MaxErrorLength);
        builder.HasIndex(a => new { a.JobId, a.StartedAt });
        builder.HasIndex(a => new { a.FinishedAt, a.Outcome });
    }
}

internal sealed class JobStateChangeConfiguration : IEntityTypeConfiguration<JobStateChange>
{
    public void Configure(EntityTypeBuilder<JobStateChange> builder)
    {
        builder.ToTable("job_state_changes");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).UseIdentityAlwaysColumn();
        builder.Property(h => h.FromState).HasConversion<string>().HasMaxLength(20);
        builder.Property(h => h.ToState).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(h => h.Reason).HasMaxLength(1000);
        builder.Property(h => h.WorkerId).HasMaxLength(200);
        builder.HasIndex(h => new { h.JobId, h.OccurredAt });
    }
}

internal sealed class RecurringJobConfiguration : IEntityTypeConfiguration<RecurringJob>
{
    public void Configure(EntityTypeBuilder<RecurringJob> builder)
    {
        builder.ToTable("recurring_jobs", table =>
        {
            table.HasCheckConstraint("ck_recurring_jobs_max_attempts", "max_attempts BETWEEN 1 AND 100");
            table.HasCheckConstraint("ck_recurring_jobs_timeout", "timeout_seconds IS NULL OR timeout_seconds > 0");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Name).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Type).HasMaxLength(JobTypeNames.MaxLength).IsRequired();
        builder.Property(r => r.Queue).HasMaxLength(QueueNames.MaxLength).IsRequired();
        builder.Property(r => r.Cron).HasMaxLength(120).IsRequired();
        builder.Property(r => r.TimeZone).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Payload).HasColumnType("jsonb");
        builder.Property(r => r.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasIndex(r => r.Name).IsUnique();
        builder.HasIndex(r => new { r.Enabled, r.NextRunAt });
    }
}

internal sealed class WorkerNodeConfiguration : IEntityTypeConfiguration<WorkerNode>
{
    public void Configure(EntityTypeBuilder<WorkerNode> builder)
    {
        builder.ToTable("workers", table => table.HasCheckConstraint("ck_workers_concurrency", "concurrency >= 0 AND running_jobs >= 0"));
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).HasMaxLength(200);
        builder.Property(w => w.HostName).HasMaxLength(255).IsRequired();
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(w => w.Queues).HasColumnType("text[]").IsRequired();
        builder.Property(w => w.JobTypes).HasColumnType("text[]").IsRequired();
        builder.Property(w => w.Version).HasMaxLength(50).IsRequired();
        builder.HasIndex(w => new { w.Status, w.LastHeartbeatAt });
    }
}

internal sealed class DeadLetterConfiguration : IEntityTypeConfiguration<DeadLetter>
{
    public void Configure(EntityTypeBuilder<DeadLetter> builder)
    {
        builder.ToTable("dead_letters");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Type).HasMaxLength(JobTypeNames.MaxLength).IsRequired();
        builder.Property(d => d.Queue).HasMaxLength(QueueNames.MaxLength).IsRequired();
        builder.Property(d => d.Payload).HasColumnType("jsonb");
        builder.Property(d => d.ErrorType).HasMaxLength(300);
        builder.Property(d => d.ErrorMessage).HasMaxLength(Limits.MaxErrorLength);
        builder.Property(d => d.StackTrace).HasMaxLength(Limits.MaxStackTraceLength);
        builder.Property(d => d.WorkerId).HasMaxLength(200);
        builder.HasIndex(d => d.FailedAt);
        builder.HasIndex(d => new { d.JobId, d.RequeuedAt });
    }
}
