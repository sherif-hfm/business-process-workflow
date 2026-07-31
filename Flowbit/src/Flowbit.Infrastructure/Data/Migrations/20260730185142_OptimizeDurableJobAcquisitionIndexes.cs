using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeDurableJobAcquisitionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_Status_LeaseExpiresAt_Id",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_Status_QueueClass_DueAt_Priority_Id",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_expired_lease_class",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "QueueClass", "Status", "LeaseExpiresAt", "Id" },
                filter: "\"Status\" IN ('running', 'resultReady') AND \"LeaseExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_runnable_class_priority_due",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "QueueClass", "Priority", "DueAt", "Id" },
                descending: new[] { false, true, false, false },
                filter: "\"Status\" IN ('queued', 'retry')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_expired_lease_class",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_runnable_class_priority_due",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_Status_LeaseExpiresAt_Id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "Status", "LeaseExpiresAt", "Id" },
                filter: "\"Status\" IN ('running', 'resultReady') AND \"LeaseExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_Status_QueueClass_DueAt_Priority_Id",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "Status", "QueueClass", "DueAt", "Priority", "Id" },
                descending: new[] { false, false, false, true, false },
                filter: "\"Status\" IN ('queued', 'retry')");
        }
    }
}
