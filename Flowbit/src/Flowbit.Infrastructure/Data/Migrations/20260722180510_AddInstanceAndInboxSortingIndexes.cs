using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceAndInboxSortingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_CreatedAt_Id",
                table: "workflow_instances",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_UpdatedAt_Id",
                table: "workflow_instances",
                columns: new[] { "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_Status_CreatedAt_Id",
                table: "user_tasks",
                columns: new[] { "Status", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_InstanceId_Id",
                table: "execution_tokens",
                columns: new[] { "InstanceId", "Id" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_CreatedAt_Id",
                table: "workflow_instances");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_UpdatedAt_Id",
                table: "workflow_instances");

            migrationBuilder.DropIndex(
                name: "IX_user_tasks_Status_CreatedAt_Id",
                table: "user_tasks");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_InstanceId_Id",
                table: "execution_tokens");
        }
    }
}
