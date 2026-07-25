using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "NodeExecutionId",
                schema: "flowbit",
                table: "instance_variables",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CurrentNodeExecutionId",
                schema: "flowbit",
                table: "execution_tokens",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "node_executions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionTokenId = table.Column<long>(type: "bigint", nullable: false),
                    UserTaskId = table.Column<long>(type: "bigint", nullable: true),
                    MultiInstanceExecutionId = table.Column<long>(type: "bigint", nullable: true),
                    ItemIndex = table.Column<int>(type: "integer", nullable: true),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    NodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NodeExternalId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    NodeType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExecutionKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CompletionReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EntryParallelBranchId = table.Column<long>(type: "bigint", nullable: true),
                    ExitParallelBranchId = table.Column<long>(type: "bigint", nullable: true),
                    EnteredViaFlowId = table.Column<int>(type: "integer", nullable: true),
                    SelectedFlowId = table.Column<int>(type: "integer", nullable: true),
                    ExitedViaFlowId = table.Column<int>(type: "integer", nullable: true),
                    NodeRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    TriggeredBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TriggeredByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CompletedByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ErrorDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsCutoverSeeded = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_executions", x => x.Id);
                    table.CheckConstraint("CK_node_executions_completion_reason", "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') AND \"CompletionReason\" IN ('normal', 'userAction', 'messageDelivery', 'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', 'parallelScopeCancelled', 'parallelJoinMerged', 'parallelFork', 'parallelJoin', 'parallelInterrupt', 'parallelInterruptSkipped')))");
                    table.CheckConstraint("CK_node_executions_execution_kind", "\"ExecutionKind\" IN ('node', 'userTaskItem')");
                    table.CheckConstraint("CK_node_executions_multi_instance_shape", "(\"ExecutionKind\" = 'node' AND \"MultiInstanceExecutionId\" IS NULL AND \"ItemIndex\" IS NULL) OR (\"ExecutionKind\" = 'userTaskItem' AND \"UserTaskId\" IS NOT NULL AND \"MultiInstanceExecutionId\" IS NOT NULL AND \"ItemIndex\" IS NOT NULL)");
                    table.CheckConstraint("CK_node_executions_status", "\"Status\" IN ('pending', 'active', 'completed', 'cancelled', 'faulted', 'merged')");
                    table.CheckConstraint("CK_node_executions_timestamp_order", "(\"StartedAt\" IS NULL OR \"StartedAt\" >= \"CreatedAt\") AND \"UpdatedAt\" >= \"CreatedAt\" AND (\"CompletedAt\" IS NULL OR \"CompletedAt\" >= COALESCE(\"StartedAt\", \"CreatedAt\"))");
                    table.CheckConstraint("CK_node_executions_timestamps", "(\"Status\" = 'pending' AND \"StartedAt\" IS NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 'active' AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 'cancelled' AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" IN ('completed', 'faulted', 'merged') AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_node_executions_execution_tokens_ExecutionTokenId",
                        column: x => x.ExecutionTokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_node_executions_multi_instance_executions_MultiInstanceExec~",
                        column: x => x.MultiInstanceExecutionId,
                        principalSchema: "flowbit",
                        principalTable: "multi_instance_executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_node_executions_parallel_gateway_branches_EntryParallelBran~",
                        column: x => x.EntryParallelBranchId,
                        principalSchema: "flowbit",
                        principalTable: "parallel_gateway_branches",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_node_executions_parallel_gateway_branches_ExitParallelBranc~",
                        column: x => x.ExitParallelBranchId,
                        principalSchema: "flowbit",
                        principalTable: "parallel_gateway_branches",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_node_executions_user_tasks_UserTaskId",
                        column: x => x.UserTaskId,
                        principalSchema: "flowbit",
                        principalTable: "user_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_node_executions_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variables_NodeExecutionId",
                schema: "flowbit",
                table: "instance_variables",
                column: "NodeExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_CurrentNodeExecutionId",
                schema: "flowbit",
                table: "execution_tokens",
                column: "CurrentNodeExecutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_CompletedAt_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "CompletedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_CreatedAt_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_EntryParallelBranchId",
                schema: "flowbit",
                table: "node_executions",
                column: "EntryParallelBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_ExecutionTokenId_Status",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "ExecutionTokenId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_ExitParallelBranchId",
                schema: "flowbit",
                table: "node_executions",
                column: "ExitParallelBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_InstanceId_UpdatedAt_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "InstanceId", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_MultiInstanceExecutionId_ItemIndex",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "MultiInstanceExecutionId", "ItemIndex" },
                unique: true,
                filter: "\"MultiInstanceExecutionId\" IS NOT NULL AND \"ItemIndex\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_NodeExternalId_Status_StartedAt_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "NodeExternalId", "Status", "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_NodeId_Status_StartedAt_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "NodeId", "Status", "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_NodeType_Status_StartedAt_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "NodeType", "Status", "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_StartedAt_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_Status_UpdatedAt_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_UpdatedAt_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_UserTaskId",
                schema: "flowbit",
                table: "node_executions",
                column: "UserTaskId",
                unique: true,
                filter: "\"UserTaskId\" IS NOT NULL");

            // Track only work that is open at the deployment cutover. Historical
            // completed rows are intentionally not reconstructed from audit data:
            // their true activation/completion boundaries are not recoverable.
            // Seeded timestamps therefore begin at cutover and are explicitly
            // marked so API consumers never mistake them for historical duration.
            migrationBuilder.Sql("""
                INSERT INTO flowbit.node_executions
                    ("InstanceId", "ExecutionTokenId", "UserTaskId",
                     "MultiInstanceExecutionId", "ItemIndex",
                     "NodeId", "NodeName", "NodeExternalId", "NodeType",
                     "ExecutionKind", "Status", "CompletionReason",
                     "EntryParallelBranchId", "ExitParallelBranchId",
                     "EnteredViaFlowId", "SelectedFlowId", "ExitedViaFlowId",
                     "NodeRolesJson", "TriggeredBy", "TriggeredByRolesJson",
                     "CompletedBy", "CompletedByRolesJson",
                     "ErrorCode", "ErrorDescription",
                     "CreatedAt", "StartedAt", "UpdatedAt", "CompletedAt",
                     "IsCutoverSeeded")
                SELECT
                    ut."InstanceId",
                    ut."TokenId",
                    ut."Id",
                    ut."MultiInstanceExecutionId",
                    ut."ItemIndex",
                    ut."NodeId",
                    ut."NodeName",
                    ut."NodeExternalId",
                    'userTask',
                    CASE WHEN ut."MultiInstanceExecutionId" IS NULL
                         THEN 'node' ELSE 'userTaskItem' END,
                    ut."Status",
                    NULL,
                    et."ParallelBranchId",
                    NULL,
                    et."ArrivedViaFlowId",
                    NULL,
                    NULL,
                    to_jsonb(ut."Roles"),
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    CURRENT_TIMESTAMP,
                    CASE WHEN ut."Status" = 'active' THEN CURRENT_TIMESTAMP ELSE NULL END,
                    CURRENT_TIMESTAMP,
                    NULL,
                    TRUE
                FROM flowbit.user_tasks ut
                JOIN flowbit.execution_tokens et ON et."Id" = ut."TokenId"
                JOIN flowbit.workflow_instances wi ON wi."Id" = ut."InstanceId"
                WHERE wi."Status" = 'running'
                  AND et."Status" = 'active'
                  AND ut."Status" IN ('active', 'pending');

                INSERT INTO flowbit.node_executions
                    ("InstanceId", "ExecutionTokenId", "UserTaskId",
                     "MultiInstanceExecutionId", "ItemIndex",
                     "NodeId", "NodeName", "NodeExternalId", "NodeType",
                     "ExecutionKind", "Status", "CompletionReason",
                     "EntryParallelBranchId", "ExitParallelBranchId",
                     "EnteredViaFlowId", "SelectedFlowId", "ExitedViaFlowId",
                     "NodeRolesJson", "TriggeredBy", "TriggeredByRolesJson",
                     "CompletedBy", "CompletedByRolesJson",
                     "ErrorCode", "ErrorDescription",
                     "CreatedAt", "StartedAt", "UpdatedAt", "CompletedAt",
                     "IsCutoverSeeded")
                SELECT
                    et."InstanceId",
                    et."Id",
                    NULL,
                    NULL,
                    NULL,
                    et."NodeId",
                    et."NodeName",
                    et."NodeExternalId",
                    et."NodeType",
                    'node',
                    'active',
                    NULL,
                    et."ParallelBranchId",
                    NULL,
                    et."ArrivedViaFlowId",
                    NULL,
                    NULL,
                    (
                        SELECT CASE
                            WHEN jsonb_typeof(flow_node -> 'roles') = 'array'
                            THEN flow_node -> 'roles'
                            ELSE '[]'::jsonb
                        END
                        FROM jsonb_array_elements(
                            CASE
                                WHEN jsonb_typeof(wd."Definition" -> 'flowNodes') = 'array'
                                THEN wd."Definition" -> 'flowNodes'
                                ELSE '[]'::jsonb
                            END
                        ) AS flow_node
                        WHERE flow_node ->> 'id' = et."NodeId"::text
                        LIMIT 1
                    ),
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    et."FaultCode",
                    et."FaultDescription",
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP,
                    NULL,
                    TRUE
                FROM flowbit.execution_tokens et
                JOIN flowbit.workflow_instances wi ON wi."Id" = et."InstanceId"
                JOIN flowbit.workflow_definitions wd ON wd."Id" = wi."WorkflowDefinitionId"
                WHERE wi."Status" = 'running'
                  AND et."Status" = 'active'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM flowbit.user_tasks ut
                      WHERE ut."TokenId" = et."Id"
                        AND ut."Status" IN ('active', 'pending')
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM flowbit.multi_instance_executions mie
                      WHERE mie."TokenId" = et."Id"
                        AND mie."Status" = 'active'
                  );

                UPDATE flowbit.execution_tokens et
                SET "CurrentNodeExecutionId" = ne."Id"
                FROM (
                    SELECT
                        candidate."ExecutionTokenId",
                        MIN(candidate."Id") AS "Id"
                    FROM flowbit.node_executions candidate
                    WHERE candidate."ExecutionKind" = 'node'
                      AND candidate."Status" = 'active'
                    GROUP BY candidate."ExecutionTokenId"
                    HAVING COUNT(*) = 1
                ) ne
                WHERE ne."ExecutionTokenId" = et."Id";
                """);

            migrationBuilder.Sql("""
                CREATE INDEX "IX_node_executions_NodeExternalId_Lower_UpdatedAt_Id"
                    ON flowbit.node_executions
                    (lower("NodeExternalId"), "UpdatedAt" DESC, "Id" DESC)
                    WHERE "NodeExternalId" IS NOT NULL;

                CREATE INDEX "IX_node_executions_TriggeredBy_Lower_UpdatedAt_Id"
                    ON flowbit.node_executions
                    (lower("TriggeredBy"), "UpdatedAt" DESC, "Id" DESC)
                    WHERE "TriggeredBy" IS NOT NULL;

                CREATE INDEX "IX_node_executions_CompletedBy_Lower_UpdatedAt_Id"
                    ON flowbit.node_executions
                    (lower("CompletedBy"), "UpdatedAt" DESC, "Id" DESC)
                    WHERE "CompletedBy" IS NOT NULL;

                CREATE INDEX "IX_user_tasks_Owner_Lower_Status_UpdatedAt_Id"
                    ON flowbit.user_tasks
                    (lower(COALESCE("Assignee", "ClaimedBy")), "Status", "UpdatedAt" DESC, "Id" DESC)
                    WHERE "Assignee" IS NOT NULL OR "ClaimedBy" IS NOT NULL;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_execution_tokens_node_executions_CurrentNodeExecutionId",
                schema: "flowbit",
                table: "execution_tokens",
                column: "CurrentNodeExecutionId",
                principalSchema: "flowbit",
                principalTable: "node_executions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_instance_variables_node_executions_NodeExecutionId",
                schema: "flowbit",
                table: "instance_variables",
                column: "NodeExecutionId",
                principalSchema: "flowbit",
                principalTable: "node_executions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS flowbit."IX_user_tasks_Owner_Lower_Status_UpdatedAt_Id";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_execution_tokens_node_executions_CurrentNodeExecutionId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_instance_variables_node_executions_NodeExecutionId",
                schema: "flowbit",
                table: "instance_variables");

            migrationBuilder.DropTable(
                name: "node_executions",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_instance_variables_NodeExecutionId",
                schema: "flowbit",
                table: "instance_variables");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_CurrentNodeExecutionId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "NodeExecutionId",
                schema: "flowbit",
                table: "instance_variables");

            migrationBuilder.DropColumn(
                name: "CurrentNodeExecutionId",
                schema: "flowbit",
                table: "execution_tokens");
        }
    }
}
