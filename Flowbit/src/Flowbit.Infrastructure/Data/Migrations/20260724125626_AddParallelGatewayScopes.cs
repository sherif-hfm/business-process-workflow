using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParallelGatewayScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ArrivedViaFlowId",
                schema: "flowbit",
                table: "execution_tokens",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ParallelBranchId",
                schema: "flowbit",
                table: "execution_tokens",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminationReason",
                schema: "flowbit",
                table: "execution_tokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "parallel_gateway_branches",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExecutionId = table.Column<long>(type: "bigint", nullable: false),
                    OriginatingFlowId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parallel_gateway_branches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "parallel_gateway_executions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    ForkNodeId = table.Column<int>(type: "integer", nullable: false),
                    ParentBranchId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CompletionReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InterruptingNodeId = table.Column<int>(type: "integer", nullable: true),
                    InterruptingTokenId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parallel_gateway_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_parallel_gateway_executions_execution_tokens_InterruptingTo~",
                        column: x => x.InterruptingTokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_parallel_gateway_executions_parallel_gateway_branches_Paren~",
                        column: x => x.ParentBranchId,
                        principalSchema: "flowbit",
                        principalTable: "parallel_gateway_branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_parallel_gateway_executions_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_InstanceId_NodeId_Status_ArrivedViaFlowId_~",
                schema: "flowbit",
                table: "execution_tokens",
                columns: new[] { "InstanceId", "NodeId", "Status", "ArrivedViaFlowId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_ParallelBranchId_Status",
                schema: "flowbit",
                table: "execution_tokens",
                columns: new[] { "ParallelBranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_history_InstanceId_TokenId_ToStepId_Id",
                schema: "flowbit",
                table: "instance_history",
                columns: new[] { "InstanceId", "TokenId", "ToStepId", "Id" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_branches_ExecutionId_Ordinal",
                schema: "flowbit",
                table: "parallel_gateway_branches",
                columns: new[] { "ExecutionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_branches_ExecutionId_OriginatingFlowId",
                schema: "flowbit",
                table: "parallel_gateway_branches",
                columns: new[] { "ExecutionId", "OriginatingFlowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_branches_ExecutionId_Status",
                schema: "flowbit",
                table: "parallel_gateway_branches",
                columns: new[] { "ExecutionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_executions_InstanceId_ForkNodeId_Status",
                schema: "flowbit",
                table: "parallel_gateway_executions",
                columns: new[] { "InstanceId", "ForkNodeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_executions_InstanceId_Status",
                schema: "flowbit",
                table: "parallel_gateway_executions",
                columns: new[] { "InstanceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_executions_InterruptingTokenId",
                schema: "flowbit",
                table: "parallel_gateway_executions",
                column: "InterruptingTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_parallel_gateway_executions_ParentBranchId_Status",
                schema: "flowbit",
                table: "parallel_gateway_executions",
                columns: new[] { "ParentBranchId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_execution_tokens_parallel_gateway_branches_ParallelBranchId",
                schema: "flowbit",
                table: "execution_tokens",
                column: "ParallelBranchId",
                principalSchema: "flowbit",
                principalTable: "parallel_gateway_branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: "FK_parallel_gateway_branches_parallel_gateway_executions_Execu~",
                schema: "flowbit",
                table: "parallel_gateway_branches",
                column: "ExecutionId",
                principalSchema: "flowbit",
                principalTable: "parallel_gateway_executions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_execution_tokens_parallel_gateway_branches_ParallelBranchId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_parallel_gateway_branches_parallel_gateway_executions_Execu~",
                schema: "flowbit",
                table: "parallel_gateway_branches");

            migrationBuilder.DropTable(
                name: "parallel_gateway_executions",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "parallel_gateway_branches",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_InstanceId_NodeId_Status_ArrivedViaFlowId_~",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_ParallelBranchId_Status",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropIndex(
                name: "IX_instance_history_InstanceId_TokenId_ToStepId_Id",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.DropColumn(
                name: "ArrivedViaFlowId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "ParallelBranchId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "TerminationReason",
                schema: "flowbit",
                table: "execution_tokens");
        }
    }
}
