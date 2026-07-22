using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveFlowbitObjectsToSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "flowbit");

            migrationBuilder.RenameTable(
                name: "workflow_settings",
                newName: "workflow_settings",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "workflow_instances",
                newName: "workflow_instances",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "workflow_idempotency_claims",
                newName: "workflow_idempotency_claims",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "workflow_definitions",
                newName: "workflow_definitions",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "workflow_business_key_scopes",
                newName: "workflow_business_key_scopes",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "workflow_business_key_claims",
                newName: "workflow_business_key_claims",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "user_tasks",
                newName: "user_tasks",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "sequence_flow_summaries",
                newName: "sequence_flow_summaries",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "sequence_flow_occurrences",
                newName: "sequence_flow_occurrences",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "multi_instance_flow_counts",
                newName: "multi_instance_flow_counts",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "multi_instance_executions",
                newName: "multi_instance_executions",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "message_delivery_receipts",
                newName: "message_delivery_receipts",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "instance_variables",
                newName: "instance_variables",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "instance_history",
                newName: "instance_history",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "execution_tokens",
                newName: "execution_tokens",
                newSchema: "flowbit");

            migrationBuilder.RenameTable(
                name: "engine_settings",
                newName: "engine_settings",
                newSchema: "flowbit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "workflow_settings",
                schema: "flowbit",
                newName: "workflow_settings");

            migrationBuilder.RenameTable(
                name: "workflow_instances",
                schema: "flowbit",
                newName: "workflow_instances");

            migrationBuilder.RenameTable(
                name: "workflow_idempotency_claims",
                schema: "flowbit",
                newName: "workflow_idempotency_claims");

            migrationBuilder.RenameTable(
                name: "workflow_definitions",
                schema: "flowbit",
                newName: "workflow_definitions");

            migrationBuilder.RenameTable(
                name: "workflow_business_key_scopes",
                schema: "flowbit",
                newName: "workflow_business_key_scopes");

            migrationBuilder.RenameTable(
                name: "workflow_business_key_claims",
                schema: "flowbit",
                newName: "workflow_business_key_claims");

            migrationBuilder.RenameTable(
                name: "user_tasks",
                schema: "flowbit",
                newName: "user_tasks");

            migrationBuilder.RenameTable(
                name: "sequence_flow_summaries",
                schema: "flowbit",
                newName: "sequence_flow_summaries");

            migrationBuilder.RenameTable(
                name: "sequence_flow_occurrences",
                schema: "flowbit",
                newName: "sequence_flow_occurrences");

            migrationBuilder.RenameTable(
                name: "multi_instance_flow_counts",
                schema: "flowbit",
                newName: "multi_instance_flow_counts");

            migrationBuilder.RenameTable(
                name: "multi_instance_executions",
                schema: "flowbit",
                newName: "multi_instance_executions");

            migrationBuilder.RenameTable(
                name: "message_delivery_receipts",
                schema: "flowbit",
                newName: "message_delivery_receipts");

            migrationBuilder.RenameTable(
                name: "instance_variables",
                schema: "flowbit",
                newName: "instance_variables");

            migrationBuilder.RenameTable(
                name: "instance_history",
                schema: "flowbit",
                newName: "instance_history");

            migrationBuilder.RenameTable(
                name: "execution_tokens",
                schema: "flowbit",
                newName: "execution_tokens");

            migrationBuilder.RenameTable(
                name: "engine_settings",
                schema: "flowbit",
                newName: "engine_settings");
        }
    }
}
