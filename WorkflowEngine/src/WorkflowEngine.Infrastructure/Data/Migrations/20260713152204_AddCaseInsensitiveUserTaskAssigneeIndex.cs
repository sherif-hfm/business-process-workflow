using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkflowEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseInsensitiveUserTaskAssigneeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_tasks_Assignee_Status_UpdatedAt_Id",
                table: "user_tasks");

            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_user_tasks_AssigneeLower_Status_UpdatedAt_Id"
                ON user_tasks (lower("Assignee"), "Status", "UpdatedAt" DESC, "Id" DESC)
                WHERE "Assignee" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_user_tasks_AssigneeLower_Status_UpdatedAt_Id\";");

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_Assignee_Status_UpdatedAt_Id",
                table: "user_tasks",
                columns: new[] { "Assignee", "Status", "UpdatedAt", "Id" });
        }
    }
}
