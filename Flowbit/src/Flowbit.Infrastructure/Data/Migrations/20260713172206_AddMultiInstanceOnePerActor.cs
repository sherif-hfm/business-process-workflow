using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiInstanceOnePerActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OnePerActor",
                table: "multi_instance_executions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                CREATE INDEX "IX_user_tasks_MIExecution_CompletedBy_ci"
                ON user_tasks ("MultiInstanceExecutionId", lower("CompletedBy"))
                WHERE "MultiInstanceExecutionId" IS NOT NULL
                  AND "CompletedBy" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX "IX_user_tasks_MIExecution_CompletedBy_ci";
                """);

            migrationBuilder.DropColumn(
                name: "OnePerActor",
                table: "multi_instance_executions");
        }
    }
}
