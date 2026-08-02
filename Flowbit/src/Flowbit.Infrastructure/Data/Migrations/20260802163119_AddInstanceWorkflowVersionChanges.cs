using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceWorkflowVersionChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_timer_subscriptions_workflow_definitions_WorkflowDefinition~",
                schema: "flowbit",
                table: "timer_subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_incidents_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_incidents");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_instances_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_jobs_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_WorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.AddColumn<long>(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "sequence_flow_occurrences",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "node_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "instance_history",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE flowbit.node_executions AS runtime_row
                SET "WorkflowDefinitionId" = instance."WorkflowDefinitionId"
                FROM flowbit.workflow_instances AS instance
                WHERE instance."Id" = runtime_row."InstanceId";

                UPDATE flowbit.instance_history AS runtime_row
                SET "WorkflowDefinitionId" = instance."WorkflowDefinitionId"
                FROM flowbit.workflow_instances AS instance
                WHERE instance."Id" = runtime_row."InstanceId";

                UPDATE flowbit.sequence_flow_occurrences AS runtime_row
                SET "WorkflowDefinitionId" = instance."WorkflowDefinitionId"
                FROM flowbit.workflow_instances AS instance
                WHERE instance."Id" = runtime_row."InstanceId";
                """);

            migrationBuilder.AlterColumn<long>(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "sequence_flow_occurrences",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "node_executions",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "instance_history",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_workflow_definitions_Id_WorkflowKey",
                schema: "flowbit",
                table: "workflow_definitions",
                columns: new[] { "Id", "WorkflowKey" });

            migrationBuilder.CreateTable(
                name: "workflow_instance_version_changes",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    SourceWorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    TargetWorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ChangedByRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_instance_version_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_changes_workflow_definitions_Sour~",
                        column: x => x.SourceWorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_changes_workflow_definitions_Targ~",
                        column: x => x.TargetWorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_instance_version_changes_workflow_instances_Instan~",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "workflow_instances",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_incidents_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "workflow_incidents",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" });

            migrationBuilder.CreateIndex(
                name: "IX_timer_subscriptions_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "timer_subscriptions",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" });

            migrationBuilder.CreateIndex(
                name: "IX_sequence_flow_occurrences_WorkflowDefinitionId",
                schema: "flowbit",
                table: "sequence_flow_occurrences",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "node_executions",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_instance_history_WorkflowDefinitionId",
                schema: "flowbit",
                table: "instance_history",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_changes_InstanceId_ChangedAt_Id",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                columns: new[] { "InstanceId", "ChangedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_changes_SourceWorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                column: "SourceWorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_changes_TargetWorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                column: "TargetWorkflowDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_instance_history_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "instance_history",
                column: "WorkflowDefinitionId",
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_node_executions_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "node_executions",
                column: "WorkflowDefinitionId",
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sequence_flow_occurrences_workflow_definitions_WorkflowDefi~",
                schema: "flowbit",
                table: "sequence_flow_occurrences",
                column: "WorkflowDefinitionId",
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_timer_subscriptions_workflow_definitions_WorkflowDefinition~",
                schema: "flowbit",
                table: "timer_subscriptions",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" },
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumns: new[] { "Id", "WorkflowKey" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_incidents_workflow_definitions_WorkflowDefinitionI~",
                schema: "flowbit",
                table: "workflow_incidents",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" },
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumns: new[] { "Id", "WorkflowKey" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_instances_workflow_definitions_WorkflowDefinitionI~",
                schema: "flowbit",
                table: "workflow_instances",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" },
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumns: new[] { "Id", "WorkflowKey" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_jobs_workflow_definitions_WorkflowDefinitionId_Wor~",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" },
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumns: new[] { "Id", "WorkflowKey" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_instance_history_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.DropForeignKey(
                name: "FK_node_executions_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_sequence_flow_occurrences_workflow_definitions_WorkflowDefi~",
                schema: "flowbit",
                table: "sequence_flow_occurrences");

            migrationBuilder.DropForeignKey(
                name: "FK_timer_subscriptions_workflow_definitions_WorkflowDefinition~",
                schema: "flowbit",
                table: "timer_subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_incidents_workflow_definitions_WorkflowDefinitionI~",
                schema: "flowbit",
                table: "workflow_incidents");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_instances_workflow_definitions_WorkflowDefinitionI~",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_jobs_workflow_definitions_WorkflowDefinitionId_Wor~",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropTable(
                name: "workflow_instance_version_changes",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropIndex(
                name: "IX_workflow_incidents_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "workflow_incidents");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_workflow_definitions_Id_WorkflowKey",
                schema: "flowbit",
                table: "workflow_definitions");

            migrationBuilder.DropIndex(
                name: "IX_timer_subscriptions_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "timer_subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_sequence_flow_occurrences_WorkflowDefinitionId",
                schema: "flowbit",
                table: "sequence_flow_occurrences");

            migrationBuilder.DropIndex(
                name: "IX_node_executions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropIndex(
                name: "IX_instance_history_WorkflowDefinitionId",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.DropColumn(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "sequence_flow_occurrences");

            migrationBuilder.DropColumn(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_WorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_instances",
                column: "WorkflowDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_timer_subscriptions_workflow_definitions_WorkflowDefinition~",
                schema: "flowbit",
                table: "timer_subscriptions",
                column: "WorkflowDefinitionId",
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_incidents_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_incidents",
                column: "WorkflowDefinitionId",
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_instances_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_instances",
                column: "WorkflowDefinitionId",
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_jobs_workflow_definitions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "WorkflowDefinitionId",
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
