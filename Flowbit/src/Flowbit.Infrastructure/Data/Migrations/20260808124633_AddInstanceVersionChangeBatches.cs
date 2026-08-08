using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceVersionChangeBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BatchId",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BatchItemId",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workflow_instance_version_change_batches",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceWorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    TargetWorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SelectionJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreparedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PreparedByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    ConfirmedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ConfirmedByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    TotalItemCount = table.Column<int>(type: "integer", nullable: false),
                    EligibleItemCount = table.Column<int>(type: "integer", nullable: false),
                    IneligibleItemCount = table.Column<int>(type: "integer", nullable: false),
                    WarningItemCount = table.Column<int>(type: "integer", nullable: false),
                    StaleItemCount = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("PK_workflow_instance_version_change_batches", x => x.Id);
                    table.CheckConstraint("CK_workflow_instance_version_change_batches_counts", "\"TotalItemCount\" >= 0 AND \"TotalItemCount\" <= 10000 AND \"EligibleItemCount\" >= 0 AND \"IneligibleItemCount\" >= 0 AND \"WarningItemCount\" >= 0 AND \"StaleItemCount\" >= 0 AND \"StaleItemCount\" <= \"IneligibleItemCount\" AND \"QueuedItemCount\" >= 0 AND \"SucceededItemCount\" >= 0 AND \"SkippedItemCount\" >= 0 AND \"FailedItemCount\" >= 0 AND \"CancelledItemCount\" >= 0");
                    table.CheckConstraint("CK_workflow_instance_version_change_batches_definitions", "\"SourceWorkflowDefinitionId\" > 0 AND \"TargetWorkflowDefinitionId\" > 0 AND \"SourceWorkflowDefinitionId\" <> \"TargetWorkflowDefinitionId\"");
                    table.CheckConstraint("CK_workflow_instance_version_change_batches_reason", "char_length(btrim(\"Reason\")) BETWEEN 1 AND 1000");
                    table.CheckConstraint("CK_workflow_instance_version_change_batches_selection", "jsonb_typeof(\"SelectionJson\") = 'object'");
                    table.CheckConstraint("CK_workflow_instance_version_change_batches_status", "\"Status\" IN ('preparing', 'ready', 'queued', 'running', 'completed', 'completedWithIssues', 'cancelled', 'failed')");
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_change_batches_workflow_definitio~",
                        column: x => x.SourceWorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_change_batches_workflow_definiti~1",
                        column: x => x.TargetWorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_change_batches_workflow_jobs_Exec~",
                        column: x => x.ExecutionJobId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_change_batches_workflow_jobs_Prep~",
                        column: x => x.PreparationJobId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "workflow_instance_version_change_batch_items",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    CapturedSourceWorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    CapturedInstanceUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BlockersJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    WarningsJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ResultJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    PreparedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_instance_version_change_batch_items", x => x.Id);
                    table.CheckConstraint("CK_workflow_instance_version_change_batch_items_identity", "\"BatchId\" > 0 AND \"InstanceId\" > 0 AND \"CapturedSourceWorkflowDefinitionId\" > 0");
                    table.CheckConstraint("CK_workflow_instance_version_change_batch_items_issues", "(\"BlockersJson\" IS NULL OR jsonb_typeof(\"BlockersJson\") = 'array') AND (\"WarningsJson\" IS NULL OR jsonb_typeof(\"WarningsJson\") = 'array')");
                    table.CheckConstraint("CK_workflow_instance_version_change_batch_items_status", "\"Status\" IN ('preparing', 'eligible', 'ineligible', 'queued', 'succeeded', 'skipped', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_change_batch_items_workflow_defin~",
                        column: x => x.CapturedSourceWorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_change_batch_items_workflow_insta~",
                        column: x => x.BatchId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instance_version_change_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_change_batch_items_workflow_inst~1",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_changes_BatchId",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_changes_BatchItemId",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                column: "BatchItemId",
                unique: true,
                filter: "\"BatchItemId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_instance_version_changes_batch_correlation",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                sql: "(\"BatchId\" IS NULL AND \"BatchItemId\" IS NULL) OR (\"BatchId\" IS NOT NULL AND \"BatchItemId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batch_items_BatchId_Instan~",
                schema: "flowbit",
                table: "workflow_instance_version_change_batch_items",
                columns: new[] { "BatchId", "InstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batch_items_BatchId_Status~",
                schema: "flowbit",
                table: "workflow_instance_version_change_batch_items",
                columns: new[] { "BatchId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batch_items_CapturedSource~",
                schema: "flowbit",
                table: "workflow_instance_version_change_batch_items",
                column: "CapturedSourceWorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batch_items_InstanceId_Id",
                schema: "flowbit",
                table: "workflow_instance_version_change_batch_items",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batches_ExecutionJobId",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                column: "ExecutionJobId",
                unique: true,
                filter: "\"ExecutionJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batches_PreparationJobId",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                column: "PreparationJobId",
                unique: true,
                filter: "\"PreparationJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batches_PreparedBy_Idempot~",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                columns: new[] { "PreparedBy", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batches_SourceWorkflowDefi~",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                columns: new[] { "SourceWorkflowDefinitionId", "TargetWorkflowDefinitionId", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batches_Status_UpdatedAt_Id",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                columns: new[] { "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batches_TargetWorkflowDefi~",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                column: "TargetWorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_change_batches_WorkflowKey_Status~",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                columns: new[] { "WorkflowKey", "Status", "UpdatedAt", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_instance_version_changes_workflow_instance_version~",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                column: "BatchId",
                principalSchema: "flowbit",
                principalTable: "workflow_instance_version_change_batches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_instance_version_changes_workflow_instance_versio~1",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                column: "BatchItemId",
                principalSchema: "flowbit",
                principalTable: "workflow_instance_version_change_batch_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT
                    'WorkflowVersionChanges',
                    'MaxBatchInstances',
                    '10000',
                    'Maximum number of running instances allowed in one workflow version-change batch. Invalid or missing values default to 10000.',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings
                    WHERE
                        ("Namespace" = 'WorkflowVersionChanges' AND "Key" = 'MaxBatchInstances')
                        OR
                        (BTRIM(COALESCE("Namespace", '')) = ''
                         AND "Key" = 'WorkflowVersionChanges.MaxBatchInstances')
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM flowbit.engine_settings
                WHERE "Namespace" = 'WorkflowVersionChanges'
                  AND "Key" = 'MaxBatchInstances'
                  AND "Value" = '10000'
                  AND "Description" = 'Maximum number of running instances allowed in one workflow version-change batch. Invalid or missing values default to 10000.';
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_instance_version_changes_workflow_instance_version~",
                schema: "flowbit",
                table: "workflow_instance_version_changes");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_instance_version_changes_workflow_instance_versio~1",
                schema: "flowbit",
                table: "workflow_instance_version_changes");

            migrationBuilder.DropTable(
                name: "workflow_instance_version_change_batch_items",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "workflow_instance_version_change_batches",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instance_version_changes_BatchId",
                schema: "flowbit",
                table: "workflow_instance_version_changes");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instance_version_changes_BatchItemId",
                schema: "flowbit",
                table: "workflow_instance_version_changes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_instance_version_changes_batch_correlation",
                schema: "flowbit",
                table: "workflow_instance_version_changes");

            migrationBuilder.DropColumn(
                name: "BatchId",
                schema: "flowbit",
                table: "workflow_instance_version_changes");

            migrationBuilder.DropColumn(
                name: "BatchItemId",
                schema: "flowbit",
                table: "workflow_instance_version_changes");
        }
    }
}
