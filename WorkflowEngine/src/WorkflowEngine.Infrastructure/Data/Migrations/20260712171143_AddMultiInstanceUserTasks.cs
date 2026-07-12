using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WorkflowEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiInstanceUserTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Assignee",
                table: "user_tasks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletedBy",
                table: "user_tasks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemIndex",
                table: "user_tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "ItemValueJson",
                table: "user_tasks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MultiInstanceExecutionId",
                table: "user_tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "ResultJson",
                table: "user_tasks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SelectedFlowId",
                table: "user_tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemIndex",
                table: "instance_history",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MultiInstanceExecutionId",
                table: "instance_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TokenId",
                table: "instance_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UserTaskId",
                table: "instance_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "multi_instance_executions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResultVariable = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalCount = table.Column<int>(type: "integer", nullable: false),
                    CompletedCount = table.Column<int>(type: "integer", nullable: false),
                    CancelledCount = table.Column<int>(type: "integer", nullable: false),
                    WinningFlowId = table.Column<int>(type: "integer", nullable: true),
                    CompletionReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_multi_instance_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_multi_instance_executions_execution_tokens_TokenId",
                        column: x => x.TokenId,
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_multi_instance_executions_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "multi_instance_flow_counts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExecutionId = table.Column<long>(type: "bigint", nullable: false),
                    FlowId = table.Column<int>(type: "integer", nullable: false),
                    CompletedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_multi_instance_flow_counts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_multi_instance_flow_counts_multi_instance_executions_Execut~",
                        column: x => x.ExecutionId,
                        principalTable: "multi_instance_executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_Assignee_Status_UpdatedAt_Id",
                table: "user_tasks",
                columns: new[] { "Assignee", "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_MultiInstanceExecutionId_ItemIndex",
                table: "user_tasks",
                columns: new[] { "MultiInstanceExecutionId", "ItemIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_MultiInstanceExecutionId_Status_ItemIndex",
                table: "user_tasks",
                columns: new[] { "MultiInstanceExecutionId", "Status", "ItemIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_executions_InstanceId_Status",
                table: "multi_instance_executions",
                columns: new[] { "InstanceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_executions_TokenId_NodeId_Status",
                table: "multi_instance_executions",
                columns: new[] { "TokenId", "NodeId", "Status" });

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "UX_multi_instance_executions_ActiveTokenNode"
                ON multi_instance_executions ("TokenId", "NodeId")
                WHERE "Status" = 'active';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_flow_counts_ExecutionId_FlowId",
                table: "multi_instance_flow_counts",
                columns: new[] { "ExecutionId", "FlowId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_user_tasks_multi_instance_executions_MultiInstanceExecution~",
                table: "user_tasks",
                column: "MultiInstanceExecutionId",
                principalTable: "multi_instance_executions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"UX_multi_instance_executions_ActiveTokenNode\";");
            migrationBuilder.DropForeignKey(
                name: "FK_user_tasks_multi_instance_executions_MultiInstanceExecution~",
                table: "user_tasks");

            migrationBuilder.DropTable(
                name: "multi_instance_flow_counts");

            migrationBuilder.DropTable(
                name: "multi_instance_executions");

            migrationBuilder.DropIndex(
                name: "IX_user_tasks_Assignee_Status_UpdatedAt_Id",
                table: "user_tasks");

            migrationBuilder.DropIndex(
                name: "IX_user_tasks_MultiInstanceExecutionId_ItemIndex",
                table: "user_tasks");

            migrationBuilder.DropIndex(
                name: "IX_user_tasks_MultiInstanceExecutionId_Status_ItemIndex",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "Assignee",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "CompletedBy",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "ItemIndex",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "ItemValueJson",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "MultiInstanceExecutionId",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "SelectedFlowId",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "ItemIndex",
                table: "instance_history");

            migrationBuilder.DropColumn(
                name: "MultiInstanceExecutionId",
                table: "instance_history");

            migrationBuilder.DropColumn(
                name: "TokenId",
                table: "instance_history");

            migrationBuilder.DropColumn(
                name: "UserTaskId",
                table: "instance_history");
        }
    }
}
