using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowJobOperationsIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_status_updated_id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "Status", "UpdatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_updated_id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "UpdatedAt", "Id" },
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_status_updated_id",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_updated_id",
                schema: "flowbit",
                table: "workflow_jobs");
        }
    }
}
