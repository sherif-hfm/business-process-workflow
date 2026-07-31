using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DetachResolvedIncidentsFromExpiredJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workflow_incidents_workflow_jobs_JobId",
                schema: "flowbit",
                table: "workflow_incidents");

            migrationBuilder.AlterColumn<long>(
                name: "JobId",
                schema: "flowbit",
                table: "workflow_incidents",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<long>(
                name: "OriginalJobId",
                schema: "flowbit",
                table: "workflow_incidents",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE flowbit.workflow_incidents
                SET "OriginalJobId" = "JobId"
                WHERE "OriginalJobId" IS NULL
                """);

            migrationBuilder.AlterColumn<long>(
                name: "OriginalJobId",
                schema: "flowbit",
                table: "workflow_incidents",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_incidents_OriginalJobId_Id",
                schema: "flowbit",
                table: "workflow_incidents",
                columns: new[] { "OriginalJobId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_incidents_job_identity",
                schema: "flowbit",
                table: "workflow_incidents",
                sql: "\"OriginalJobId\" > 0 AND (\"Status\" <> 'open' OR \"JobId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_incidents_workflow_jobs_JobId",
                schema: "flowbit",
                table: "workflow_incidents",
                column: "JobId",
                principalSchema: "flowbit",
                principalTable: "workflow_jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workflow_incidents_workflow_jobs_JobId",
                schema: "flowbit",
                table: "workflow_incidents");

            migrationBuilder.DropIndex(
                name: "IX_workflow_incidents_OriginalJobId_Id",
                schema: "flowbit",
                table: "workflow_incidents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_incidents_job_identity",
                schema: "flowbit",
                table: "workflow_incidents");

            migrationBuilder.DropColumn(
                name: "OriginalJobId",
                schema: "flowbit",
                table: "workflow_incidents");

            // A detached resolved incident cannot be reattached after its
            // retained job has been deleted. Remove those historical rows
            // before restoring the old required/cascading relationship.
            migrationBuilder.Sql(
                """
                DELETE FROM flowbit.workflow_incidents
                WHERE "JobId" IS NULL
                """);

            migrationBuilder.AlterColumn<long>(
                name: "JobId",
                schema: "flowbit",
                table: "workflow_incidents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_incidents_workflow_jobs_JobId",
                schema: "flowbit",
                table: "workflow_incidents",
                column: "JobId",
                principalSchema: "flowbit",
                principalTable: "workflow_jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
