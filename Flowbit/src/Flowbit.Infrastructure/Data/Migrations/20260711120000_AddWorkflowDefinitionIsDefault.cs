using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowDefinitionIsDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "workflow_definitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: for each WorkflowKey that has at least one published version,
            // mark the highest-version published row as the default. For keys with no
            // published version, mark the highest-version row as the default so there
            // is always exactly one default per workflow key.
            migrationBuilder.Sql("""
                UPDATE workflow_definitions d
                SET "IsDefault" = true
                WHERE d."Id" = (
                    SELECT s."Id"
                    FROM workflow_definitions s
                    WHERE s."WorkflowKey" = d."WorkflowKey"
                    ORDER BY s."IsPublished" DESC, s."Version" DESC
                    LIMIT 1
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "workflow_definitions");
        }
    }
}
