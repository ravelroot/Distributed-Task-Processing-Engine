using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Turno.Infrastructure.Persistence;

#nullable disable

namespace Turno.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(TurnoDbContext))]
    [Migration("20261016230434_Inicial")]
    partial class Inicial
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasDefaultSchema("turno")
                .HasAnnotation("ProductVersion", "10.0.12")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("MassTransit.EntityFrameworkCoreIntegration.InboxState", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));

                    b.Property<DateTime?>("Consumed")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("consumed");

                    b.Property<Guid>("ConsumerId")
                        .HasColumnType("uuid")
                        .HasColumnName("consumer_id");

                    b.Property<DateTime?>("Delivered")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("delivered");

                    b.Property<DateTime?>("ExpirationTime")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("expiration_time");

                    b.Property<long?>("LastSequenceNumber")
                        .HasColumnType("bigint")
                        .HasColumnName("last_sequence_number");

                    b.Property<Guid>("LockId")
                        .HasColumnType("uuid")
                        .HasColumnName("lock_id");

                    b.Property<Guid>("MessageId")
                        .HasColumnType("uuid")
                        .HasColumnName("message_id");

                    b.Property<int>("ReceiveCount")
                        .HasColumnType("integer")
                        .HasColumnName("receive_count");

                    b.Property<DateTime>("Received")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("received");

                    b.Property<byte[]>("RowVersion")
                        .IsConcurrencyToken()
                        .ValueGeneratedOnAddOrUpdate()
                        .HasColumnType("bytea")
                        .HasColumnName("row_version");

                    b.HasKey("Id")
                        .HasName("pk_inbox_state");

                    b.HasAlternateKey("MessageId", "ConsumerId")
                        .HasName("ak_inbox_state_message_id_consumer_id");

                    b.HasIndex("Delivered")
                        .HasDatabaseName("ix_inbox_state_delivered");

                    b.ToTable("inbox_state", "turno");
                });

            modelBuilder.Entity("MassTransit.EntityFrameworkCoreIntegration.OutboxMessage", b =>
                {
                    b.Property<long>("SequenceNumber")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint")
                        .HasColumnName("sequence_number");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("SequenceNumber"));

                    b.Property<string>("Body")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("body");

                    b.Property<string>("ContentType")
                        .IsRequired()
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)")
                        .HasColumnName("content_type");

                    b.Property<Guid?>("ConversationId")
                        .HasColumnType("uuid")
                        .HasColumnName("conversation_id");

                    b.Property<Guid?>("CorrelationId")
                        .HasColumnType("uuid")
                        .HasColumnName("correlation_id");

                    b.Property<string>("DestinationAddress")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)")
                        .HasColumnName("destination_address");

                    b.Property<DateTime?>("EnqueueTime")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("enqueue_time");

                    b.Property<DateTime?>("ExpirationTime")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("expiration_time");

                    b.Property<string>("FaultAddress")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)")
                        .HasColumnName("fault_address");

                    b.Property<string>("Headers")
                        .HasColumnType("text")
                        .HasColumnName("headers");

                    b.Property<Guid?>("InboxConsumerId")
                        .HasColumnType("uuid")
                        .HasColumnName("inbox_consumer_id");

                    b.Property<Guid?>("InboxMessageId")
                        .HasColumnType("uuid")
                        .HasColumnName("inbox_message_id");

                    b.Property<Guid?>("InitiatorId")
                        .HasColumnType("uuid")
                        .HasColumnName("initiator_id");

                    b.Property<Guid>("MessageId")
                        .HasColumnType("uuid")
                        .HasColumnName("message_id");

                    b.Property<string>("MessageType")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("message_type");

                    b.Property<Guid?>("OutboxId")
                        .HasColumnType("uuid")
                        .HasColumnName("outbox_id");

                    b.Property<string>("Properties")
                        .HasColumnType("text")
                        .HasColumnName("properties");

                    b.Property<Guid?>("RequestId")
                        .HasColumnType("uuid")
                        .HasColumnName("request_id");

                    b.Property<string>("ResponseAddress")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)")
                        .HasColumnName("response_address");

                    b.Property<DateTime>("SentTime")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("sent_time");

                    b.Property<string>("SourceAddress")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)")
                        .HasColumnName("source_address");

                    b.HasKey("SequenceNumber")
                        .HasName("pk_outbox_message");

                    b.HasIndex("EnqueueTime")
                        .HasDatabaseName("ix_outbox_message_enqueue_time");

                    b.HasIndex("ExpirationTime")
                        .HasDatabaseName("ix_outbox_message_expiration_time");

                    b.HasIndex("OutboxId", "SequenceNumber")
                        .IsUnique()
                        .HasDatabaseName("ix_outbox_message_outbox_id_sequence_number");

                    b.HasIndex("InboxMessageId", "InboxConsumerId", "SequenceNumber")
                        .IsUnique()
                        .HasDatabaseName("ix_outbox_message_inbox_message_id_inbox_consumer_id_sequence_~");

                    b.ToTable("outbox_message", "turno");
                });

            modelBuilder.Entity("MassTransit.EntityFrameworkCoreIntegration.OutboxState", b =>
                {
                    b.Property<Guid>("OutboxId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("outbox_id");

                    b.Property<DateTime>("Created")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created");

                    b.Property<DateTime?>("Delivered")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("delivered");

                    b.Property<long?>("LastSequenceNumber")
                        .HasColumnType("bigint")
                        .HasColumnName("last_sequence_number");

                    b.Property<Guid>("LockId")
                        .HasColumnType("uuid")
                        .HasColumnName("lock_id");

                    b.Property<byte[]>("RowVersion")
                        .IsConcurrencyToken()
                        .ValueGeneratedOnAddOrUpdate()
                        .HasColumnType("bytea")
                        .HasColumnName("row_version");

                    b.HasKey("OutboxId")
                        .HasName("pk_outbox_state");

                    b.HasIndex("Created")
                        .HasDatabaseName("ix_outbox_state_created");

                    b.ToTable("outbox_state", "turno");
                });

            modelBuilder.Entity("Turno.Core.Domain.DeadLetter", b =>
                {
                    b.Property<Guid>("Id")
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<int>("Attempts")
                        .HasColumnType("integer")
                        .HasColumnName("attempts");

                    b.Property<string>("ErrorMessage")
                        .HasMaxLength(4000)
                        .HasColumnType("character varying(4000)")
                        .HasColumnName("error_message");

                    b.Property<string>("ErrorType")
                        .HasMaxLength(300)
                        .HasColumnType("character varying(300)")
                        .HasColumnName("error_type");

                    b.Property<DateTimeOffset>("FailedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("failed_at");

                    b.Property<Guid>("JobId")
                        .HasColumnType("uuid")
                        .HasColumnName("job_id");

                    b.Property<string>("Payload")
                        .HasColumnType("jsonb")
                        .HasColumnName("payload");

                    b.Property<string>("Queue")
                        .IsRequired()
                        .HasMaxLength(63)
                        .HasColumnType("character varying(63)")
                        .HasColumnName("queue");

                    b.Property<DateTimeOffset?>("RequeuedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("requeued_at");

                    b.Property<string>("StackTrace")
                        .HasMaxLength(16000)
                        .HasColumnType("character varying(16000)")
                        .HasColumnName("stack_trace");

                    b.Property<string>("Type")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("type");

                    b.Property<string>("WorkerId")
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("worker_id");

                    b.HasKey("Id")
                        .HasName("pk_dead_letters");

                    b.HasIndex("FailedAt")
                        .HasDatabaseName("ix_dead_letters_failed_at");

                    b.HasIndex("JobId", "RequeuedAt")
                        .HasDatabaseName("ix_dead_letters_job_id_requeued_at");

                    b.ToTable("dead_letters", "turno");
                });

            modelBuilder.Entity("Turno.Core.Domain.Job", b =>
                {
                    b.Property<Guid>("Id")
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<int>("Attempt")
                        .HasColumnType("integer")
                        .HasColumnName("attempt");

                    b.Property<bool>("CancellationRequested")
                        .HasColumnType("boolean")
                        .HasColumnName("cancellation_requested");

                    b.Property<string>("ConcurrencyKey")
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("concurrency_key");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<Guid?>("DispatchId")
                        .HasColumnType("uuid")
                        .HasColumnName("dispatch_id");

                    b.Property<DateTimeOffset?>("EnqueuedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("enqueued_at");

                    b.Property<DateTimeOffset?>("FinishedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("finished_at");

                    b.Property<string>("IdempotencyKey")
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("idempotency_key");

                    b.Property<string>("LastError")
                        .HasMaxLength(4000)
                        .HasColumnType("character varying(4000)")
                        .HasColumnName("last_error");

                    b.Property<DateTimeOffset?>("LeaseExpiresAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("lease_expires_at");

                    b.Property<int>("MaxAttempts")
                        .HasColumnType("integer")
                        .HasColumnName("max_attempts");

                    b.Property<string>("Payload")
                        .HasColumnType("jsonb")
                        .HasColumnName("payload");

                    b.Property<int?>("Progress")
                        .HasColumnType("integer")
                        .HasColumnName("progress");

                    b.Property<string>("Queue")
                        .IsRequired()
                        .HasMaxLength(63)
                        .HasColumnType("character varying(63)")
                        .HasColumnName("queue");

                    b.Property<Guid?>("RecurringJobId")
                        .HasColumnType("uuid")
                        .HasColumnName("recurring_job_id");

                    b.Property<string>("Result")
                        .HasColumnType("jsonb")
                        .HasColumnName("result");

                    b.Property<DateTimeOffset?>("ScheduledAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("scheduled_at");

                    b.Property<DateTimeOffset?>("StartedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("started_at");

                    b.Property<string>("State")
                        .IsRequired()
                        .HasMaxLength(20)
                        .HasColumnType("character varying(20)")
                        .HasColumnName("state");

                    b.Property<int?>("TimeoutSeconds")
                        .HasColumnType("integer")
                        .HasColumnName("timeout_seconds");

                    b.Property<string>("Type")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("type");

                    b.Property<string>("WorkerId")
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("worker_id");

                    b.HasKey("Id")
                        .HasName("pk_jobs");

                    b.HasIndex("CreatedAt")
                        .HasDatabaseName("ix_jobs_created_at");

                    b.HasIndex("IdempotencyKey")
                        .IsUnique()
                        .HasDatabaseName("ix_jobs_idempotency_key");

                    b.HasIndex("RecurringJobId")
                        .HasDatabaseName("ix_jobs_recurring_job_id");

                    b.HasIndex("WorkerId")
                        .HasDatabaseName("ix_jobs_worker_id");

                    b.HasIndex("Queue", "State")
                        .HasDatabaseName("ix_jobs_queue_state");

                    b.HasIndex("State", "FinishedAt")
                        .HasDatabaseName("ix_jobs_state_finished_at");

                    b.HasIndex("State", "LeaseExpiresAt")
                        .HasDatabaseName("ix_jobs_state_lease_expires_at");

                    b.HasIndex("State", "ScheduledAt")
                        .HasDatabaseName("ix_jobs_state_scheduled_at");

                    b.HasIndex("Type", "CreatedAt")
                        .HasDatabaseName("ix_jobs_type_created_at");

                    b.ToTable("jobs", "turno", t =>
                        {
                            t.HasCheckConstraint("ck_jobs_attempt", "attempt >= 0");

                            t.HasCheckConstraint("ck_jobs_max_attempts", "max_attempts BETWEEN 1 AND 100");

                            t.HasCheckConstraint("ck_jobs_progress", "progress IS NULL OR progress BETWEEN 0 AND 100");

                            t.HasCheckConstraint("ck_jobs_timeout", "timeout_seconds IS NULL OR timeout_seconds > 0");
                        });
                });

            modelBuilder.Entity("Turno.Core.Domain.JobAttempt", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityAlwaysColumn(b.Property<long>("Id"));

                    b.Property<long?>("DurationMs")
                        .HasColumnType("bigint")
                        .HasColumnName("duration_ms");

                    b.Property<string>("ErrorMessage")
                        .HasMaxLength(4000)
                        .HasColumnType("character varying(4000)")
                        .HasColumnName("error_message");

                    b.Property<string>("ErrorType")
                        .HasMaxLength(300)
                        .HasColumnType("character varying(300)")
                        .HasColumnName("error_type");

                    b.Property<DateTimeOffset?>("FinishedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("finished_at");

                    b.Property<Guid>("JobId")
                        .HasColumnType("uuid")
                        .HasColumnName("job_id");

                    b.Property<int>("Number")
                        .HasColumnType("integer")
                        .HasColumnName("number");

                    b.Property<string>("Outcome")
                        .IsRequired()
                        .HasMaxLength(20)
                        .HasColumnType("character varying(20)")
                        .HasColumnName("outcome");

                    b.Property<DateTimeOffset>("StartedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("started_at");

                    b.Property<string>("WorkerId")
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("worker_id");

                    b.HasKey("Id")
                        .HasName("pk_job_attempts");

                    b.HasIndex("FinishedAt", "Outcome")
                        .HasDatabaseName("ix_job_attempts_finished_at_outcome");

                    b.HasIndex("JobId", "StartedAt")
                        .HasDatabaseName("ix_job_attempts_job_id_started_at");

                    b.ToTable("job_attempts", "turno", t =>
                        {
                            t.HasCheckConstraint("ck_job_attempts_number", "number >= 1");
                        });
                });

            modelBuilder.Entity("Turno.Core.Domain.JobStateChange", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityAlwaysColumn(b.Property<long>("Id"));

                    b.Property<string>("FromState")
                        .HasMaxLength(20)
                        .HasColumnType("character varying(20)")
                        .HasColumnName("from_state");

                    b.Property<Guid>("JobId")
                        .HasColumnType("uuid")
                        .HasColumnName("job_id");

                    b.Property<DateTimeOffset>("OccurredAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("occurred_at");

                    b.Property<string>("Reason")
                        .HasMaxLength(1000)
                        .HasColumnType("character varying(1000)")
                        .HasColumnName("reason");

                    b.Property<string>("ToState")
                        .IsRequired()
                        .HasMaxLength(20)
                        .HasColumnType("character varying(20)")
                        .HasColumnName("to_state");

                    b.Property<string>("WorkerId")
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("worker_id");

                    b.HasKey("Id")
                        .HasName("pk_job_state_changes");

                    b.HasIndex("JobId", "OccurredAt")
                        .HasDatabaseName("ix_job_state_changes_job_id_occurred_at");

                    b.ToTable("job_state_changes", "turno");
                });

            modelBuilder.Entity("Turno.Core.Domain.RecurringJob", b =>
                {
                    b.Property<Guid>("Id")
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<bool>("AllowConcurrent")
                        .HasColumnType("boolean")
                        .HasColumnName("allow_concurrent");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<string>("Cron")
                        .IsRequired()
                        .HasMaxLength(120)
                        .HasColumnType("character varying(120)")
                        .HasColumnName("cron");

                    b.Property<bool>("Enabled")
                        .HasColumnType("boolean")
                        .HasColumnName("enabled");

                    b.Property<Guid?>("LastJobId")
                        .HasColumnType("uuid")
                        .HasColumnName("last_job_id");

                    b.Property<DateTimeOffset?>("LastRunAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("last_run_at");

                    b.Property<int>("MaxAttempts")
                        .HasColumnType("integer")
                        .HasColumnName("max_attempts");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("name");

                    b.Property<DateTimeOffset?>("NextRunAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("next_run_at");

                    b.Property<string>("Payload")
                        .HasColumnType("jsonb")
                        .HasColumnName("payload");

                    b.Property<string>("Queue")
                        .IsRequired()
                        .HasMaxLength(63)
                        .HasColumnType("character varying(63)")
                        .HasColumnName("queue");

                    b.Property<string>("TimeZone")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("time_zone");

                    b.Property<int?>("TimeoutSeconds")
                        .HasColumnType("integer")
                        .HasColumnName("timeout_seconds");

                    b.Property<string>("Type")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("type");

                    b.Property<DateTimeOffset>("UpdatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("updated_at");

                    b.Property<uint>("Version")
                        .IsConcurrencyToken()
                        .ValueGeneratedOnAddOrUpdate()
                        .HasColumnType("xid")
                        .HasColumnName("xmin");

                    b.HasKey("Id")
                        .HasName("pk_recurring_jobs");

                    b.HasIndex("Name")
                        .IsUnique()
                        .HasDatabaseName("ix_recurring_jobs_name");

                    b.HasIndex("Enabled", "NextRunAt")
                        .HasDatabaseName("ix_recurring_jobs_enabled_next_run_at");

                    b.ToTable("recurring_jobs", "turno", t =>
                        {
                            t.HasCheckConstraint("ck_recurring_jobs_max_attempts", "max_attempts BETWEEN 1 AND 100");

                            t.HasCheckConstraint("ck_recurring_jobs_timeout", "timeout_seconds IS NULL OR timeout_seconds > 0");
                        });
                });

            modelBuilder.Entity("Turno.Core.Domain.WorkerNode", b =>
                {
                    b.Property<string>("Id")
                        .HasMaxLength(200)
                        .HasColumnType("character varying(200)")
                        .HasColumnName("id");

                    b.Property<int>("Concurrency")
                        .HasColumnType("integer")
                        .HasColumnName("concurrency");

                    b.Property<long>("FailedJobs")
                        .HasColumnType("bigint")
                        .HasColumnName("failed_jobs");

                    b.Property<string>("HostName")
                        .IsRequired()
                        .HasMaxLength(255)
                        .HasColumnType("character varying(255)")
                        .HasColumnName("host_name");

                    b.PrimitiveCollection<string[]>("JobTypes")
                        .IsRequired()
                        .HasColumnType("text[]")
                        .HasColumnName("job_types");

                    b.Property<DateTimeOffset>("LastHeartbeatAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("last_heartbeat_at");

                    b.Property<int>("ProcessId")
                        .HasColumnType("integer")
                        .HasColumnName("process_id");

                    b.Property<long>("ProcessedJobs")
                        .HasColumnType("bigint")
                        .HasColumnName("processed_jobs");

                    b.PrimitiveCollection<string[]>("Queues")
                        .IsRequired()
                        .HasColumnType("text[]")
                        .HasColumnName("queues");

                    b.Property<int>("RunningJobs")
                        .HasColumnType("integer")
                        .HasColumnName("running_jobs");

                    b.Property<DateTimeOffset>("StartedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("started_at");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(20)
                        .HasColumnType("character varying(20)")
                        .HasColumnName("status");

                    b.Property<string>("Version")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("character varying(50)")
                        .HasColumnName("version");

                    b.HasKey("Id")
                        .HasName("pk_workers");

                    b.HasIndex("Status", "LastHeartbeatAt")
                        .HasDatabaseName("ix_workers_status_last_heartbeat_at");

                    b.ToTable("workers", "turno", t =>
                        {
                            t.HasCheckConstraint("ck_workers_concurrency", "concurrency >= 0 AND running_jobs >= 0");
                        });
                });

            modelBuilder.Entity("MassTransit.EntityFrameworkCoreIntegration.OutboxMessage", b =>
                {
                    b.HasOne("MassTransit.EntityFrameworkCoreIntegration.OutboxState", null)
                        .WithMany()
                        .HasForeignKey("OutboxId")
                        .HasConstraintName("fk_outbox_message_outbox_state_outbox_id");

                    b.HasOne("MassTransit.EntityFrameworkCoreIntegration.InboxState", null)
                        .WithMany()
                        .HasForeignKey("InboxMessageId", "InboxConsumerId")
                        .HasPrincipalKey("MessageId", "ConsumerId")
                        .HasConstraintName("fk_outbox_message_inbox_state_inbox_message_id_inbox_consumer_~");
                });

            modelBuilder.Entity("Turno.Core.Domain.DeadLetter", b =>
                {
                    b.HasOne("Turno.Core.Domain.Job", "Job")
                        .WithMany("DeadLetters")
                        .HasForeignKey("JobId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_dead_letters_jobs_job_id");

                    b.Navigation("Job");
                });

            modelBuilder.Entity("Turno.Core.Domain.Job", b =>
                {
                    b.HasOne("Turno.Core.Domain.RecurringJob", "RecurringJob")
                        .WithMany("Jobs")
                        .HasForeignKey("RecurringJobId")
                        .OnDelete(DeleteBehavior.SetNull)
                        .HasConstraintName("fk_jobs_recurring_jobs_recurring_job_id");

                    b.HasOne("Turno.Core.Domain.WorkerNode", "Worker")
                        .WithMany("Jobs")
                        .HasForeignKey("WorkerId")
                        .OnDelete(DeleteBehavior.SetNull)
                        .HasConstraintName("fk_jobs_workers_worker_id");

                    b.Navigation("RecurringJob");

                    b.Navigation("Worker");
                });

            modelBuilder.Entity("Turno.Core.Domain.JobAttempt", b =>
                {
                    b.HasOne("Turno.Core.Domain.Job", "Job")
                        .WithMany("Attempts")
                        .HasForeignKey("JobId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_job_attempts_jobs_job_id");

                    b.Navigation("Job");
                });

            modelBuilder.Entity("Turno.Core.Domain.JobStateChange", b =>
                {
                    b.HasOne("Turno.Core.Domain.Job", "Job")
                        .WithMany("History")
                        .HasForeignKey("JobId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired()
                        .HasConstraintName("fk_job_state_changes_jobs_job_id");

                    b.Navigation("Job");
                });

            modelBuilder.Entity("Turno.Core.Domain.Job", b =>
                {
                    b.Navigation("Attempts");

                    b.Navigation("DeadLetters");

                    b.Navigation("History");
                });

            modelBuilder.Entity("Turno.Core.Domain.RecurringJob", b =>
                {
                    b.Navigation("Jobs");
                });

            modelBuilder.Entity("Turno.Core.Domain.WorkerNode", b =>
                {
                    b.Navigation("Jobs");
                });
#pragma warning restore 612, 618
        }
    }
}
