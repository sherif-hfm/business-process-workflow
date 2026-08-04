using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdministrativeActionBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions",
                sql: "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR "
                    + "(\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') "
                    + "AND \"CompletionReason\" IN "
                    + "('normal', 'userAction', 'administrativeAction', 'messageDelivery', "
                    + "'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', "
                    + "'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', "
                    + "'instanceCancelled', 'gatewayScopeCancelled', 'gatewayJoinMerged', "
                    + "'parallelFork', 'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', "
                    + "'complexActivation', 'complexReset', 'scopedInterrupt', "
                    + "'scopedInterruptSkipped', 'timerFired')))");

            migrationBuilder.AddColumn<long>(
                name: "AdministrativeActionBatchId",
                schema: "flowbit",
                table: "user_tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionKind",
                schema: "flowbit",
                table: "user_tasks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionReason",
                schema: "flowbit",
                table: "user_tasks",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AdministrativeActionBatchId",
                schema: "flowbit",
                table: "instance_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                schema: "flowbit",
                table: "instance_history",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "administrative_action_batches",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FlowMappingsJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CommonVariablesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    SelectionJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreparedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PreparedByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    ConfirmedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ConfirmedByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    TotalItemCount = table.Column<int>(type: "integer", nullable: false),
                    EligibleItemCount = table.Column<int>(type: "integer", nullable: false),
                    IneligibleItemCount = table.Column<int>(type: "integer", nullable: false),
                    QueuedItemCount = table.Column<int>(type: "integer", nullable: false),
                    SucceededItemCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedItemCount = table.Column<int>(type: "integer", nullable: false),
                    FailedItemCount = table.Column<int>(type: "integer", nullable: false),
                    CancelledItemCount = table.Column<int>(type: "integer", nullable: false),
                    IssuesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    PreparationJobId = table.Column<long>(type: "bigint", nullable: true),
                    ExecutionJobId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    CancelledBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    PreparedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrative_action_batches", x => x.Id);
                    table.CheckConstraint("CK_administrative_action_batches_counts", "\"TotalItemCount\" >= 0 AND \"TotalItemCount\" <= 10000 AND \"EligibleItemCount\" >= 0 AND \"IneligibleItemCount\" >= 0 AND \"QueuedItemCount\" >= 0 AND \"SucceededItemCount\" >= 0 AND \"SkippedItemCount\" >= 0 AND \"FailedItemCount\" >= 0 AND \"CancelledItemCount\" >= 0");
                    table.CheckConstraint("CK_administrative_action_batches_flow_mappings", "jsonb_typeof(\"FlowMappingsJson\") = 'array' AND jsonb_array_length(\"FlowMappingsJson\") > 0");
                    table.CheckConstraint("CK_administrative_action_batches_status", "\"Status\" IN ('preparing', 'ready', 'queued', 'running', 'completed', 'completedWithIssues', 'cancelled', 'failed')");
                    table.ForeignKey(
                        name: "FK_administrative_action_batches_workflow_jobs_ExecutionJobId",
                        column: x => x.ExecutionJobId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_administrative_action_batches_workflow_jobs_PreparationJobId",
                        column: x => x.PreparationJobId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "administrative_action_batch_items",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    UserTaskId = table.Column<long>(type: "bigint", nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    FlowId = table.Column<int>(type: "integer", nullable: false),
                    CapturedInstanceUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CapturedUserTaskUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IssuesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ResultJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NewUserTaskId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    PreparedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrative_action_batch_items", x => x.Id);
                    table.CheckConstraint("CK_administrative_action_batch_items_status", "\"Status\" IN ('preparing', 'eligible', 'ineligible', 'queued', 'succeeded', 'skipped', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_administrative_action_batch_items_administrative_action_bat~",
                        column: x => x.BatchId,
                        principalSchema: "flowbit",
                        principalTable: "administrative_action_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_administrative_action_batch_items_execution_tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_administrative_action_batch_items_user_tasks_NewUserTaskId",
                        column: x => x.NewUserTaskId,
                        principalSchema: "flowbit",
                        principalTable: "user_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_administrative_action_batch_items_user_tasks_UserTaskId",
                        column: x => x.UserTaskId,
                        principalSchema: "flowbit",
                        principalTable: "user_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_administrative_action_batch_items_workflow_definitions_Work~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_administrative_action_batch_items_workflow_instances_Instan~",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_AdministrativeActionBatchId",
                schema: "flowbit",
                table: "user_tasks",
                column: "AdministrativeActionBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_instance_history_AdministrativeActionBatchId",
                schema: "flowbit",
                table: "instance_history",
                column: "AdministrativeActionBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batch_items_BatchId_Status_Id",
                schema: "flowbit",
                table: "administrative_action_batch_items",
                columns: new[] { "BatchId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batch_items_BatchId_UserTaskId",
                schema: "flowbit",
                table: "administrative_action_batch_items",
                columns: new[] { "BatchId", "UserTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batch_items_InstanceId_Id",
                schema: "flowbit",
                table: "administrative_action_batch_items",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batch_items_NewUserTaskId",
                schema: "flowbit",
                table: "administrative_action_batch_items",
                column: "NewUserTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batch_items_WorkflowDefinitionId_Flow~",
                schema: "flowbit",
                table: "administrative_action_batch_items",
                columns: new[] { "WorkflowDefinitionId", "FlowId" });

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batch_items_TokenId",
                schema: "flowbit",
                table: "administrative_action_batch_items",
                column: "TokenId");

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batch_items_UserTaskId",
                schema: "flowbit",
                table: "administrative_action_batch_items",
                column: "UserTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batches_ExecutionJobId",
                schema: "flowbit",
                table: "administrative_action_batches",
                column: "ExecutionJobId",
                unique: true,
                filter: "\"ExecutionJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batches_PreparationJobId",
                schema: "flowbit",
                table: "administrative_action_batches",
                column: "PreparationJobId",
                unique: true,
                filter: "\"PreparationJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batches_PreparedBy_IdempotencyKey",
                schema: "flowbit",
                table: "administrative_action_batches",
                columns: new[] { "PreparedBy", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batches_Status_UpdatedAt_Id",
                schema: "flowbit",
                table: "administrative_action_batches",
                columns: new[] { "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_administrative_action_batches_WorkflowKey_Status_UpdatedAt_~",
                schema: "flowbit",
                table: "administrative_action_batches",
                columns: new[] { "WorkflowKey", "Status", "UpdatedAt", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_instance_history_administrative_action_batches_Administrati~",
                schema: "flowbit",
                table: "instance_history",
                column: "AdministrativeActionBatchId",
                principalSchema: "flowbit",
                principalTable: "administrative_action_batches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_user_tasks_administrative_action_batches_AdministrativeActi~",
                schema: "flowbit",
                table: "user_tasks",
                column: "AdministrativeActionBatchId",
                principalSchema: "flowbit",
                principalTable: "administrative_action_batches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                WITH desired("Namespace", "Key", "FullKey", "Value", "Description") AS
                (
                    VALUES
                        (
                            'WorkflowBatchActions',
                            'RequiredRole',
                            'WorkflowBatchActions.RequiredRole',
                            'admin',
                            'Comma-separated roles required to prepare, confirm, cancel, and monitor administrative action batches. Missing or blank values default to admin.'
                        ),
                        (
                            'WorkflowBatchActions',
                            'MaxItems',
                            'WorkflowBatchActions.MaxItems',
                            '10000',
                            'Maximum number of frozen user tasks allowed in one administrative action batch. Invalid or missing values default to 10000.'
                        )
                )
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT
                    desired."Namespace",
                    desired."Key",
                    desired."Value",
                    desired."Description",
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM desired
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings AS existing
                    WHERE
                        (existing."Namespace" = desired."Namespace"
                         AND existing."Key" = desired."Key")
                        OR
                        (BTRIM(COALESCE(existing."Namespace", '')) = ''
                         AND existing."Key" = desired."FullKey")
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);

            migrationBuilder.Sql("""
                WITH desired("Namespace", "Key", "FullKey", "Description") AS
                (
                    VALUES
                        (
                            'WorkflowBatchActions',
                            'RequiredRole',
                            'WorkflowBatchActions.RequiredRole',
                            'Comma-separated roles required to prepare, confirm, cancel, and monitor administrative action batches. Missing or blank values default to admin.'
                        ),
                        (
                            'WorkflowBatchActions',
                            'MaxItems',
                            'WorkflowBatchActions.MaxItems',
                            'Maximum number of frozen user tasks allowed in one administrative action batch. Invalid or missing values default to 10000.'
                        )
                )
                UPDATE flowbit.engine_settings AS existing
                SET "Description" = desired."Description"
                FROM desired
                WHERE existing."Description" IS NULL
                  AND
                  (
                      (existing."Namespace" = desired."Namespace"
                       AND existing."Key" = desired."Key")
                      OR
                      (BTRIM(COALESCE(existing."Namespace", '')) = ''
                       AND existing."Key" = desired."FullKey")
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            // Preserve downgrade operability after this completion reason has
            // been written by mapping it to the closest legacy audit reason.
            migrationBuilder.Sql(
                "UPDATE flowbit.node_executions "
                + "SET \"CompletionReason\" = 'userAction' "
                + "WHERE \"CompletionReason\" = 'administrativeAction';");

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions",
                sql: "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR "
                    + "(\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') "
                    + "AND \"CompletionReason\" IN "
                    + "('normal', 'userAction', 'messageDelivery', 'multiInstanceItem', "
                    + "'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', "
                    + "'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', "
                    + "'gatewayScopeCancelled', 'gatewayJoinMerged', 'parallelFork', "
                    + "'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', "
                    + "'complexActivation', 'complexReset', 'scopedInterrupt', "
                    + "'scopedInterruptSkipped', 'timerFired')))");

            migrationBuilder.DropForeignKey(
                name: "FK_instance_history_administrative_action_batches_Administrati~",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.DropForeignKey(
                name: "FK_user_tasks_administrative_action_batches_AdministrativeActi~",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropTable(
                name: "administrative_action_batch_items",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "administrative_action_batches",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_user_tasks_AdministrativeActionBatchId",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropIndex(
                name: "IX_instance_history_AdministrativeActionBatchId",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.DropColumn(
                name: "AdministrativeActionBatchId",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "CompletionKind",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "CompletionReason",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "AdministrativeActionBatchId",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.DropColumn(
                name: "Reason",
                schema: "flowbit",
                table: "instance_history");
        }
    }
}
