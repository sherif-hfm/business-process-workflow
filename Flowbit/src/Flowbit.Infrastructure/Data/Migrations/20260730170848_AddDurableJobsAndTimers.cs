using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableJobsAndTimers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.AddColumn<Guid>(
                name: "ActivationId",
                schema: "flowbit",
                table: "execution_tokens",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<string>(
                name: "WaitState",
                schema: "flowbit",
                table: "execution_tokens",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WaitingJobId",
                schema: "flowbit",
                table: "execution_tokens",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WaitingTimerSubscriptionId",
                schema: "flowbit",
                table: "execution_tokens",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "timer_subscriptions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: true),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: true),
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimerNodeId = table.Column<int>(type: "integer", nullable: false),
                    TimerNodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AttachedToNodeId = table.Column<int>(type: "integer", nullable: true),
                    ScheduleKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ScheduleExpression = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CancelActivity = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NextDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Occurrence = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timer_subscriptions", x => x.Id);
                    table.CheckConstraint("CK_timer_subscriptions_occurrence", "\"Occurrence\" >= 0");
                    table.CheckConstraint("CK_timer_subscriptions_schedule_kind", "\"ScheduleKind\" IN ('timeDate', 'timeDuration', 'timeCycle')");
                    table.CheckConstraint("CK_timer_subscriptions_status", "\"Status\" IN ('active', 'paused', 'completed', 'cancelled')");
                    table.CheckConstraint("CK_timer_subscriptions_terminal_time", "(\"Status\" IN ('active', 'paused') AND \"CompletedAt\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled') AND \"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_timer_subscriptions_execution_tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_timer_subscriptions_workflow_definitions_WorkflowDefinition~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_timer_subscriptions_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_job_snapshots",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InvocationJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    VariablesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    OutputVariableVersionsJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    FlowInfoJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    EvaluationTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_job_snapshots", x => x.Id);
                    table.CheckConstraint("CK_workflow_job_snapshots_size", "\"SizeBytes\" >= 0 AND \"SizeBytes\" <= 1048576");
                });

            migrationBuilder.CreateTable(
                name: "workflow_jobs",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: true),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: true),
                    MultiInstanceExecutionId = table.Column<long>(type: "bigint", nullable: true),
                    UserTaskId = table.Column<long>(type: "bigint", nullable: true),
                    TimerSubscriptionId = table.Column<long>(type: "bigint", nullable: true),
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    NodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NodeType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    QueueClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Phase = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    FailureHandling = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RetryDelays = table.Column<TimeSpan[]>(type: "interval[]", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScheduledOccurrenceAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PayloadJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    SnapshotId = table.Column<long>(type: "bigint", nullable: true),
                    WorkerId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ErrorJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ResultReadyAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastFailureDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_jobs", x => x.Id);
                    table.CheckConstraint("CK_workflow_jobs_attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" > 0 AND \"AttemptCount\" <= \"MaxAttempts\"");
                    table.CheckConstraint("CK_workflow_jobs_lease_shape", "((\"Status\" IN ('running', 'resultReady') AND \"WorkerId\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" NOT IN ('running', 'resultReady') AND \"WorkerId\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseExpiresAt\" IS NULL))");
                    table.CheckConstraint("CK_workflow_jobs_queue_class", "\"QueueClass\" IN ('control', 'activity')");
                    table.CheckConstraint("CK_workflow_jobs_status", "\"Status\" IN ('queued', 'running', 'resultReady', 'retry', 'completed', 'incident', 'cancelled', 'skipped')");
                    table.CheckConstraint("CK_workflow_jobs_terminal_time", "(\"Status\" IN ('completed', 'cancelled', 'skipped') AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" NOT IN ('completed', 'cancelled', 'skipped') AND \"CompletedAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_workflow_jobs_execution_tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_jobs_multi_instance_executions_MultiInstanceExecut~",
                        column: x => x.MultiInstanceExecutionId,
                        principalSchema: "flowbit",
                        principalTable: "multi_instance_executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_jobs_timer_subscriptions_TimerSubscriptionId",
                        column: x => x.TimerSubscriptionId,
                        principalSchema: "flowbit",
                        principalTable: "timer_subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_jobs_user_tasks_UserTaskId",
                        column: x => x.UserTaskId,
                        principalSchema: "flowbit",
                        principalTable: "user_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_jobs_workflow_definitions_WorkflowDefinitionId",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_jobs_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workflow_jobs_workflow_job_snapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_job_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_incidents",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<long>(type: "bigint", nullable: true),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    NodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_incidents", x => x.Id);
                    table.CheckConstraint("CK_workflow_incidents_resolution", "(\"Status\" = 'open' AND \"ResolvedAt\" IS NULL AND \"ResolvedBy\" IS NULL) OR (\"Status\" = 'resolved' AND \"ResolvedAt\" IS NOT NULL AND \"ResolvedBy\" IS NOT NULL)");
                    table.CheckConstraint("CK_workflow_incidents_status", "\"Status\" IN ('open', 'resolved')");
                    table.ForeignKey(
                        name: "FK_workflow_incidents_workflow_definitions_WorkflowDefinitionId",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_incidents_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workflow_incidents_workflow_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_job_attempts",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<long>(type: "bigint", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkerId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_job_attempts", x => x.Id);
                    table.CheckConstraint("CK_workflow_job_attempts_number", "\"AttemptNumber\" > 0 AND \"LeaseGeneration\" > 0");
                    table.CheckConstraint("CK_workflow_job_attempts_status", "\"Status\" IN ('running', 'resultReady', 'completed', 'failed', 'leaseLost', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_workflow_job_attempts_workflow_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions",
                sql: "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') AND \"CompletionReason\" IN ('normal', 'userAction', 'messageDelivery', 'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', 'gatewayScopeCancelled', 'gatewayJoinMerged', 'parallelFork', 'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', 'complexActivation', 'complexReset', 'scopedInterrupt', 'scopedInterruptSkipped', 'timerFired')))");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_WaitingJobId",
                schema: "flowbit",
                table: "execution_tokens",
                column: "WaitingJobId",
                filter: "\"WaitingJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_WaitingTimerSubscriptionId",
                schema: "flowbit",
                table: "execution_tokens",
                column: "WaitingTimerSubscriptionId",
                filter: "\"WaitingTimerSubscriptionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_execution_tokens_wait_shape",
                schema: "flowbit",
                table: "execution_tokens",
                sql: "(\"WaitState\" IS NULL AND \"WaitingJobId\" IS NULL AND \"WaitingTimerSubscriptionId\" IS NULL) OR (\"WaitState\" IS NOT NULL AND (\"WaitingJobId\" IS NOT NULL OR \"WaitingTimerSubscriptionId\" IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_timer_subscriptions_InstanceId_TokenId_ActivationId_TimerNo~",
                schema: "flowbit",
                table: "timer_subscriptions",
                columns: new[] { "InstanceId", "TokenId", "ActivationId", "TimerNodeId" },
                unique: true,
                filter: "\"InstanceId\" IS NOT NULL AND \"TokenId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_timer_subscriptions_Status_NextDueAt_Id",
                schema: "flowbit",
                table: "timer_subscriptions",
                columns: new[] { "Status", "NextDueAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_timer_subscriptions_TokenId",
                schema: "flowbit",
                table: "timer_subscriptions",
                column: "TokenId");

            migrationBuilder.CreateIndex(
                name: "IX_timer_subscriptions_WorkflowDefinitionId_TimerNodeId",
                schema: "flowbit",
                table: "timer_subscriptions",
                columns: new[] { "WorkflowDefinitionId", "TimerNodeId" },
                unique: true,
                filter: "\"InstanceId\" IS NULL AND \"Status\" IN ('active', 'paused')");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_incidents_InstanceId_Status_Id",
                schema: "flowbit",
                table: "workflow_incidents",
                columns: new[] { "InstanceId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_incidents_JobId",
                schema: "flowbit",
                table: "workflow_incidents",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_incidents_open_job",
                schema: "flowbit",
                table: "workflow_incidents",
                columns: new[] { "JobId", "Status" },
                unique: true,
                filter: "\"Status\" = 'open'");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_incidents_ResolvedAt_Id",
                schema: "flowbit",
                table: "workflow_incidents",
                columns: new[] { "ResolvedAt", "Id" },
                filter: "\"Status\" = 'resolved'");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_incidents_Status_UpdatedAt_Id",
                schema: "flowbit",
                table: "workflow_incidents",
                columns: new[] { "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_incidents_WorkflowDefinitionId_Status_Id",
                schema: "flowbit",
                table: "workflow_incidents",
                columns: new[] { "WorkflowDefinitionId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_job_attempts_JobId_AttemptNumber",
                schema: "flowbit",
                table: "workflow_job_attempts",
                columns: new[] { "JobId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_job_attempts_JobId_Id",
                schema: "flowbit",
                table: "workflow_job_attempts",
                columns: new[] { "JobId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_job_snapshots_CreatedAt",
                schema: "flowbit",
                table: "workflow_job_snapshots",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_CompletedAt_Id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "CompletedAt", "Id" },
                filter: "\"Status\" IN ('completed', 'cancelled', 'skipped')");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_InstanceId_Status_Id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "InstanceId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_MultiInstanceExecutionId",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "MultiInstanceExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_SnapshotId",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_Status_LeaseExpiresAt_Id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "Status", "LeaseExpiresAt", "Id" },
                filter: "\"Status\" IN ('running', 'resultReady') AND \"LeaseExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_Status_QueueClass_DueAt_Priority_Id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "Status", "QueueClass", "DueAt", "Priority", "Id" },
                descending: new[] { false, false, false, true, false },
                filter: "\"Status\" IN ('queued', 'retry')");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_TimerSubscriptionId_ScheduledOccurrenceAt",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "TimerSubscriptionId", "ScheduledOccurrenceAt" },
                unique: true,
                filter: "\"TimerSubscriptionId\" IS NOT NULL AND \"ScheduledOccurrenceAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_TokenId_Status_Id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "TokenId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_UserTaskId",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "UserTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_WorkflowDefinitionId_Status_Id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "WorkflowDefinitionId", "Status", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_incidents",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "workflow_job_attempts",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "workflow_jobs",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "timer_subscriptions",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "workflow_job_snapshots",
                schema: "flowbit");

            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_WaitingJobId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_WaitingTimerSubscriptionId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropCheckConstraint(
                name: "CK_execution_tokens_wait_shape",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "ActivationId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "WaitState",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "WaitingJobId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "WaitingTimerSubscriptionId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions",
                sql: "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') AND \"CompletionReason\" IN ('normal', 'userAction', 'messageDelivery', 'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', 'gatewayScopeCancelled', 'gatewayJoinMerged', 'parallelFork', 'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', 'complexActivation', 'complexReset', 'scopedInterrupt', 'scopedInterruptSkipped')))");
        }
    }
}
