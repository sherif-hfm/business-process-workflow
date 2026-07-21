using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRequiredTaskAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresAssignment",
                table: "user_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_InstanceId_Status_CompletedAt_Id",
                table: "user_tasks",
                columns: new[] { "InstanceId", "Status", "CompletedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_tasks_InstanceId_Status_CompletedAt_Id",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "RequiresAssignment",
                table: "user_tasks");
        }
    }
}
