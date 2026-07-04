using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkflowEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowDefinitionWorkflowKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WorkflowKey",
                table: "workflow_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill existing rows from the JSON model id stored in the JSONB definition.
            migrationBuilder.Sql(
                "UPDATE workflow_definitions SET \"WorkflowKey\" = COALESCE(NULLIF(\"Definition\" #>> '{id}', '')::int, 0);");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_WorkflowKey",
                table: "workflow_definitions",
                column: "WorkflowKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_definitions_WorkflowKey",
                table: "workflow_definitions");

            migrationBuilder.DropColumn(
                name: "WorkflowKey",
                table: "workflow_definitions");
        }
    }
}
