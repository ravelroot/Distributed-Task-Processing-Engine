using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Turno.Infrastructure.Persistence.Migrations
{
    public partial class Inicial : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "turno");

            migrationBuilder.CreateTable(
                name: "inbox_state",
                schema: "turno",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lock_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    receive_count = table.Column<int>(type: "integer", nullable: false),
                    expiration_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_sequence_number = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_state", x => x.id);
                    table.UniqueConstraint("ak_inbox_state_message_id_consumer_id", x => new { x.message_id, x.consumer_id });
                });

            migrationBuilder.CreateTable(
                name: "outbox_state",
                schema: "turno",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lock_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_sequence_number = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_state", x => x.outbox_id);
                });

            migrationBuilder.CreateTable(
                name: "recurring_jobs",
                schema: "turno",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    queue = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    cron = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    allow_concurrent = table.Column<bool>(type: "boolean", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recurring_jobs", x => x.id);
                    table.CheckConstraint("ck_recurring_jobs_max_attempts", "max_attempts BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_recurring_jobs_timeout", "timeout_seconds IS NULL OR timeout_seconds > 0");
                });

            migrationBuilder.CreateTable(
                name: "workers",
                schema: "turno",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    host_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    process_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    queues = table.Column<string[]>(type: "text[]", nullable: false),
                    job_types = table.Column<string[]>(type: "text[]", nullable: false),
                    concurrency = table.Column<int>(type: "integer", nullable: false),
                    running_jobs = table.Column<int>(type: "integer", nullable: false),
                    processed_jobs = table.Column<long>(type: "bigint", nullable: false),
                    failed_jobs = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workers", x => x.id);
                    table.CheckConstraint("ck_workers_concurrency", "concurrency >= 0 AND running_jobs >= 0");
                });

            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "turno",
                columns: table => new
                {
                    sequence_number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    enqueue_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    headers = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    inbox_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inbox_consumer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    initiator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    destination_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    response_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    fault_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    expiration_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_message", x => x.sequence_number);
                    table.ForeignKey(
                        name: "fk_outbox_message_inbox_state_inbox_message_id_inbox_consumer_~",
                        columns: x => new { x.inbox_message_id, x.inbox_consumer_id },
                        principalSchema: "turno",
                        principalTable: "inbox_state",
                        principalColumns: new[] { "message_id", "consumer_id" });
                    table.ForeignKey(
                        name: "fk_outbox_message_outbox_state_outbox_id",
                        column: x => x.outbox_id,
                        principalSchema: "turno",
                        principalTable: "outbox_state",
                        principalColumn: "outbox_id");
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "turno",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    queue = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: true),
                    progress = table.Column<int>(type: "integer", nullable: true),
                    dispatch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    worker_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellation_requested = table.Column<bool>(type: "boolean", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    concurrency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    recurring_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enqueued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_attempt", "attempt >= 0");
                    table.CheckConstraint("ck_jobs_max_attempts", "max_attempts BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_jobs_progress", "progress IS NULL OR progress BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_jobs_timeout", "timeout_seconds IS NULL OR timeout_seconds > 0");
                    table.ForeignKey(
                        name: "fk_jobs_recurring_jobs_recurring_job_id",
                        column: x => x.recurring_job_id,
                        principalSchema: "turno",
                        principalTable: "recurring_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_jobs_workers_worker_id",
                        column: x => x.worker_id,
                        principalSchema: "turno",
                        principalTable: "workers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "dead_letters",
                schema: "turno",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    queue = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error_type = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    stack_trace = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    worker_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requeued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dead_letters", x => x.id);
                    table.ForeignKey(
                        name: "fk_dead_letters_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "turno",
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "job_attempts",
                schema: "turno",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    worker_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    error_type = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_attempts", x => x.id);
                    table.CheckConstraint("ck_job_attempts_number", "number >= 1");
                    table.ForeignKey(
                        name: "fk_job_attempts_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "turno",
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "job_state_changes",
                schema: "turno",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    worker_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_state_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_job_state_changes_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "turno",
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dead_letters_failed_at",
                schema: "turno",
                table: "dead_letters",
                column: "failed_at");

            migrationBuilder.CreateIndex(
                name: "ix_dead_letters_job_id_requeued_at",
                schema: "turno",
                table: "dead_letters",
                columns: new[] { "job_id", "requeued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_state_delivered",
                schema: "turno",
                table: "inbox_state",
                column: "delivered");

            migrationBuilder.CreateIndex(
                name: "ix_job_attempts_finished_at_outcome",
                schema: "turno",
                table: "job_attempts",
                columns: new[] { "finished_at", "outcome" });

            migrationBuilder.CreateIndex(
                name: "ix_job_attempts_job_id_started_at",
                schema: "turno",
                table: "job_attempts",
                columns: new[] { "job_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_job_state_changes_job_id_occurred_at",
                schema: "turno",
                table: "job_state_changes",
                columns: new[] { "job_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_created_at",
                schema: "turno",
                table: "jobs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_idempotency_key",
                schema: "turno",
                table: "jobs",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_queue_state",
                schema: "turno",
                table: "jobs",
                columns: new[] { "queue", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_recurring_job_id",
                schema: "turno",
                table: "jobs",
                column: "recurring_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_state_finished_at",
                schema: "turno",
                table: "jobs",
                columns: new[] { "state", "finished_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_state_lease_expires_at",
                schema: "turno",
                table: "jobs",
                columns: new[] { "state", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_state_scheduled_at",
                schema: "turno",
                table: "jobs",
                columns: new[] { "state", "scheduled_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_type_created_at",
                schema: "turno",
                table: "jobs",
                columns: new[] { "type", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_worker_id",
                schema: "turno",
                table: "jobs",
                column: "worker_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_enqueue_time",
                schema: "turno",
                table: "outbox_message",
                column: "enqueue_time");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_expiration_time",
                schema: "turno",
                table: "outbox_message",
                column: "expiration_time");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_inbox_message_id_inbox_consumer_id_sequence_~",
                schema: "turno",
                table: "outbox_message",
                columns: new[] { "inbox_message_id", "inbox_consumer_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_outbox_id_sequence_number",
                schema: "turno",
                table: "outbox_message",
                columns: new[] { "outbox_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_state_created",
                schema: "turno",
                table: "outbox_state",
                column: "created");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_jobs_enabled_next_run_at",
                schema: "turno",
                table: "recurring_jobs",
                columns: new[] { "enabled", "next_run_at" });

            migrationBuilder.CreateIndex(
                name: "ix_recurring_jobs_name",
                schema: "turno",
                table: "recurring_jobs",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workers_status_last_heartbeat_at",
                schema: "turno",
                table: "workers",
                columns: new[] { "status", "last_heartbeat_at" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dead_letters",
                schema: "turno");

            migrationBuilder.DropTable(
                name: "job_attempts",
                schema: "turno");

            migrationBuilder.DropTable(
                name: "job_state_changes",
                schema: "turno");

            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "turno");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "turno");

            migrationBuilder.DropTable(
                name: "inbox_state",
                schema: "turno");

            migrationBuilder.DropTable(
                name: "outbox_state",
                schema: "turno");

            migrationBuilder.DropTable(
                name: "recurring_jobs",
                schema: "turno");

            migrationBuilder.DropTable(
                name: "workers",
                schema: "turno");
        }
    }
}
