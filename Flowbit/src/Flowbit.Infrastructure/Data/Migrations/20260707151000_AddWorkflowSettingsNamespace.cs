using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowSettingsNamespace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_settings_Name",
                table: "workflow_settings");

            migrationBuilder.AddColumn<string>(
                name: "Namespace",
                table: "workflow_settings",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_settings_Namespace_Name",
                table: "workflow_settings",
                columns: new[] { "Namespace", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_settings_Namespace_Name",
                table: "workflow_settings");

            migrationBuilder.DropColumn(
                name: "Namespace",
                table: "workflow_settings");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_settings_Name",
                table: "workflow_settings",
                column: "Name",
                unique: true);
        }
    }
}
