using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenWorkflowVersionFamilies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_definitions_Name_Version",
                table: "workflow_definitions");

            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "WorkflowKey"
                               ORDER BY "Version", "CreatedAt", "Id")::integer AS new_version
                    FROM workflow_definitions
                )
                UPDATE workflow_definitions AS definition
                SET "Version" = ranked.new_version
                FROM ranked
                WHERE definition."Id" = ranked."Id"
                  AND definition."Version" <> ranked.new_version;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_WorkflowKey_Version",
                table: "workflow_definitions",
                columns: new[] { "WorkflowKey", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_definitions_WorkflowKey_Version",
                table: "workflow_definitions");

            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "Name"
                               ORDER BY "Version", "CreatedAt", "Id")::integer AS new_version
                    FROM workflow_definitions
                )
                UPDATE workflow_definitions AS definition
                SET "Version" = ranked.new_version
                FROM ranked
                WHERE definition."Id" = ranked."Id"
                  AND definition."Version" <> ranked.new_version;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_Name_Version",
                table: "workflow_definitions",
                columns: new[] { "Name", "Version" },
                unique: true);
        }
    }
}
