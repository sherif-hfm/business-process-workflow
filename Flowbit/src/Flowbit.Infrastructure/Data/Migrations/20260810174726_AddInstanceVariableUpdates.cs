using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceVariableUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InstanceVariableUpdateAuditId",
                schema: "flowbit",
                table: "instance_variables",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "instance_variable_update_batches",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    VariablesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    SelectionJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreparedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PreparedByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    ConfirmedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ConfirmedByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    TotalItemCount = table.Column<int>(type: "integer", nullable: false),
                    EligibleItemCount = table.Column<int>(type: "integer", nullable: false),
                    IneligibleItemCount = table.Column<int>(type: "integer", nullable: false),
                    WarningItemCount = table.Column<int>(type: "integer", nullable: false),
                    QueuedItemCount = table.Column<int>(type: "integer", nullable: false),
                    SucceededItemCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedItemCount = table.Column<int>(type: "integer", nullable: false),
                    FailedItemCount = table.Column<int>(type: "integer", nullable: false),
                    CancelledItemCount = table.Column<int>(type: "integer", nullable: false),
                    IssuesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
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
                    table.PrimaryKey("PK_instance_variable_update_batches", x => x.Id);
                    table.CheckConstraint("CK_instance_variable_update_batches_counts", "\"TotalItemCount\" >= 0 AND \"TotalItemCount\" <= 10000 AND \"EligibleItemCount\" >= 0 AND \"IneligibleItemCount\" >= 0 AND \"WarningItemCount\" >= 0 AND \"QueuedItemCount\" >= 0 AND \"SucceededItemCount\" >= 0 AND \"SkippedItemCount\" >= 0 AND \"FailedItemCount\" >= 0 AND \"CancelledItemCount\" >= 0");
                    table.CheckConstraint("CK_instance_variable_update_batches_selection", "jsonb_typeof(\"SelectionJson\") = 'object'");
                    table.CheckConstraint("CK_instance_variable_update_batches_status", "\"Status\" IN ('preparing', 'ready', 'queued', 'running', 'completed', 'completedWithIssues', 'cancelled', 'failed')");
                    table.CheckConstraint("CK_instance_variable_update_batches_variables", "jsonb_typeof(\"VariablesJson\") = 'array' AND jsonb_array_length(\"VariablesJson\") BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "instance_variable_update_batch_jobs",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    Phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OriginalJobId = table.Column<long>(type: "bigint", nullable: false),
                    JobId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_variable_update_batch_jobs", x => x.Id);
                    table.CheckConstraint("CK_instance_variable_update_batch_jobs_identity", "\"BatchId\" > 0 AND \"WorkflowDefinitionId\" > 0 AND \"OriginalJobId\" > 0");
                    table.CheckConstraint("CK_instance_variable_update_batch_jobs_phase", "\"Phase\" IN ('prepare', 'execute')");
                    table.ForeignKey(
                        name: "FK_instance_variable_update_batch_jobs_instance_variable_updat~",
                        column: x => x.BatchId,
                        principalSchema: "flowbit",
                        principalTable: "instance_variable_update_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_instance_variable_update_batch_jobs_workflow_definitions_Wo~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_instance_variable_update_batch_jobs_workflow_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "instance_variable_update_batch_items",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    CapturedWorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    CapturedInstanceUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PlanJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
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
                    table.PrimaryKey("PK_instance_variable_update_batch_items", x => x.Id);
                    table.UniqueConstraint(
                        "AK_instance_variable_update_batch_items_Id_BatchId_InstanceId",
                        x => new { x.Id, x.BatchId, x.InstanceId });
                    table.CheckConstraint("CK_instance_variable_update_batch_items_identity", "\"BatchId\" > 0 AND \"InstanceId\" > 0 AND \"CapturedWorkflowDefinitionId\" > 0");
                    table.CheckConstraint("CK_instance_variable_update_batch_items_json", "(\"PlanJson\" IS NULL OR jsonb_typeof(\"PlanJson\") = 'array') AND (\"WarningsJson\" IS NULL OR jsonb_typeof(\"WarningsJson\") = 'array')");
                    table.CheckConstraint("CK_instance_variable_update_batch_items_status", "\"Status\" IN ('preparing', 'eligible', 'ineligible', 'queued', 'succeeded', 'skipped', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_instance_variable_update_batch_items_instance_variable_upda~",
                        column: x => x.BatchId,
                        principalSchema: "flowbit",
                        principalTable: "instance_variable_update_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_instance_variable_update_batch_items_workflow_definitions_C~",
                        column: x => x.CapturedWorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_instance_variable_update_batch_items_workflow_instances_Ins~",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "instance_variable_updates",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    PerformedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PerformedByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestedVariablesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    ResultJson = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    BatchId = table.Column<long>(type: "bigint", nullable: true),
                    BatchItemId = table.Column<long>(type: "bigint", nullable: true),
                    PerformedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_variable_updates", x => x.Id);
                    table.UniqueConstraint(
                        "AK_instance_variable_updates_Id_InstanceId",
                        x => new { x.Id, x.InstanceId });
                    table.CheckConstraint("CK_instance_variable_updates_batch_correlation", "(\"BatchId\" IS NULL AND \"BatchItemId\" IS NULL) OR (\"BatchId\" IS NOT NULL AND \"BatchItemId\" IS NOT NULL)");
                    table.CheckConstraint("CK_instance_variable_updates_requested_variables", "jsonb_typeof(\"RequestedVariablesJson\") = 'array'");
                    table.ForeignKey(
                        name: "FK_instance_variable_updates_instance_variable_update_batch_it~",
                        columns: x => new { x.BatchItemId, x.BatchId, x.InstanceId },
                        principalSchema: "flowbit",
                        principalTable: "instance_variable_update_batch_items",
                        principalColumns: new[] { "Id", "BatchId", "InstanceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_instance_variable_updates_instance_variable_update_batches_~",
                        column: x => x.BatchId,
                        principalSchema: "flowbit",
                        principalTable: "instance_variable_update_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_instance_variable_updates_workflow_definitions_WorkflowDefi~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_instance_variable_updates_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variables_InstanceVariableUpdateAuditId_InstanceId",
                schema: "flowbit",
                table: "instance_variables",
                columns: new[] { "InstanceVariableUpdateAuditId", "InstanceId" },
                filter: "\"InstanceVariableUpdateAuditId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batch_items_BatchId_CapturedWorkfl~",
                schema: "flowbit",
                table: "instance_variable_update_batch_items",
                columns: new[] { "BatchId", "CapturedWorkflowDefinitionId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batch_items_BatchId_InstanceId",
                schema: "flowbit",
                table: "instance_variable_update_batch_items",
                columns: new[] { "BatchId", "InstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batch_items_BatchId_Status_Id",
                schema: "flowbit",
                table: "instance_variable_update_batch_items",
                columns: new[] { "BatchId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batch_items_CapturedWorkflowDefini~",
                schema: "flowbit",
                table: "instance_variable_update_batch_items",
                column: "CapturedWorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batch_items_InstanceId_Id",
                schema: "flowbit",
                table: "instance_variable_update_batch_items",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batch_jobs_BatchId_WorkflowDefinit~",
                schema: "flowbit",
                table: "instance_variable_update_batch_jobs",
                columns: new[] { "BatchId", "WorkflowDefinitionId", "Phase" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batch_jobs_JobId",
                schema: "flowbit",
                table: "instance_variable_update_batch_jobs",
                column: "JobId",
                unique: true,
                filter: "\"JobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batch_jobs_OriginalJobId",
                schema: "flowbit",
                table: "instance_variable_update_batch_jobs",
                column: "OriginalJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batch_jobs_WorkflowDefinitionId",
                schema: "flowbit",
                table: "instance_variable_update_batch_jobs",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batches_PreparedBy_IdempotencyKey",
                schema: "flowbit",
                table: "instance_variable_update_batches",
                columns: new[] { "PreparedBy", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batches_Status_UpdatedAt_Id",
                schema: "flowbit",
                table: "instance_variable_update_batches",
                columns: new[] { "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_update_batches_WorkflowKey_Status_Updated~",
                schema: "flowbit",
                table: "instance_variable_update_batches",
                columns: new[] { "WorkflowKey", "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_updates_BatchId",
                schema: "flowbit",
                table: "instance_variable_updates",
                column: "BatchId",
                filter: "\"BatchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_updates_BatchItemId_BatchId_InstanceId",
                schema: "flowbit",
                table: "instance_variable_updates",
                columns: new[] { "BatchItemId", "BatchId", "InstanceId" },
                unique: true,
                filter: "\"BatchItemId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_updates_InstanceId_PerformedAt_Id",
                schema: "flowbit",
                table: "instance_variable_updates",
                columns: new[] { "InstanceId", "PerformedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_updates_InstanceId_PerformedBy_Idempotenc~",
                schema: "flowbit",
                table: "instance_variable_updates",
                columns: new[] { "InstanceId", "PerformedBy", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_updates_WorkflowDefinitionId",
                schema: "flowbit",
                table: "instance_variable_updates",
                column: "WorkflowDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_instance_variables_instance_variable_updates_InstanceVariab~",
                schema: "flowbit",
                table: "instance_variables",
                columns: new[] { "InstanceVariableUpdateAuditId", "InstanceId" },
                principalSchema: "flowbit",
                principalTable: "instance_variable_updates",
                principalColumns: new[] { "Id", "InstanceId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION flowbit.validate_instance_variable_update_batch_job_definition()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NEW."JobId" IS NOT NULL
                       AND NOT EXISTS
                       (
                           SELECT 1
                           FROM flowbit.workflow_jobs AS job
                           WHERE job."Id" = NEW."JobId"
                             AND job."WorkflowDefinitionId" = NEW."WorkflowDefinitionId"
                       )
                    THEN
                        RAISE EXCEPTION
                            'Variable-update batch job % does not belong to workflow definition %.',
                            NEW."JobId",
                            NEW."WorkflowDefinitionId"
                            USING ERRCODE = '23503',
                                  CONSTRAINT = 'FK_instance_variable_update_batch_jobs_live_job_definition';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER "TR_instance_variable_update_batch_jobs_validate_live_job"
                AFTER INSERT OR UPDATE OF "JobId", "WorkflowDefinitionId"
                ON flowbit.instance_variable_update_batch_jobs
                DEFERRABLE INITIALLY IMMEDIATE
                FOR EACH ROW
                EXECUTE FUNCTION flowbit.validate_instance_variable_update_batch_job_definition();
                """);

            migrationBuilder.Sql("""
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT
                    'WorkflowVariableUpdates',
                    'MaxBatchInstances',
                    '10000',
                    'Maximum running workflow instances that can be frozen into one administrative variable-update batch. Invalid or missing values default to 10000 and values above 10000 are capped.',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings AS existing
                    WHERE
                        (existing."Namespace" = 'WorkflowVariableUpdates'
                         AND existing."Key" = 'MaxBatchInstances')
                        OR
                        (BTRIM(COALESCE(existing."Namespace", '')) = ''
                         AND existing."Key" = 'WorkflowVariableUpdates.MaxBatchInstances')
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Preserve the operator-owned WorkflowVariableUpdates setting. The
            // migration cannot distinguish its default from a customized value.
            migrationBuilder.DropForeignKey(
                name: "FK_instance_variables_instance_variable_updates_InstanceVariab~",
                schema: "flowbit",
                table: "instance_variables");

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_instance_variable_update_batch_jobs_validate_live_job"
                    ON flowbit.instance_variable_update_batch_jobs;
                DROP FUNCTION IF EXISTS flowbit.validate_instance_variable_update_batch_job_definition();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_instance_variable_update_batch_items_instance_variable_upda~",
                schema: "flowbit",
                table: "instance_variable_update_batch_items");

            migrationBuilder.DropForeignKey(
                name: "FK_instance_variable_updates_instance_variable_update_batches_~",
                schema: "flowbit",
                table: "instance_variable_updates");

            migrationBuilder.DropTable(
                name: "instance_variable_update_batch_jobs",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "instance_variable_update_batches",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "instance_variable_updates",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "instance_variable_update_batch_items",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_instance_variables_InstanceVariableUpdateAuditId_InstanceId",
                schema: "flowbit",
                table: "instance_variables");

            migrationBuilder.DropColumn(
                name: "InstanceVariableUpdateAuditId",
                schema: "flowbit",
                table: "instance_variables");
        }
    }
}
