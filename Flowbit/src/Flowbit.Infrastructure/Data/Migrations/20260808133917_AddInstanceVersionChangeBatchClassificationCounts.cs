using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceVersionChangeBatchClassificationCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_instance_version_change_batches_counts",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches");

            migrationBuilder.AddColumn<int>(
                name: "BlockedItemCount",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE flowbit.workflow_instance_version_change_batches AS batch
                SET "BlockedItemCount" = item_counts."BlockedItemCount",
                    "StaleItemCount" = item_counts."StaleItemCount"
                FROM (
                    SELECT candidate_batch."Id" AS "BatchId",
                           count(item."Id") FILTER (
                               WHERE item."Status" = 'ineligible'
                                 AND (item."ErrorCode" IS NULL OR item."ErrorCode" NOT IN (
                                     'stale_since_selection',
                                     'stale_since_preparation',
                                     'stale'))
                           )::integer AS "BlockedItemCount",
                           count(item."Id") FILTER (
                               WHERE item."Status" IN ('ineligible', 'skipped')
                                 AND item."ErrorCode" IN (
                                     'stale_since_selection',
                                     'stale_since_preparation',
                                     'stale')
                           )::integer AS "StaleItemCount"
                    FROM flowbit.workflow_instance_version_change_batches AS candidate_batch
                    LEFT JOIN flowbit.workflow_instance_version_change_batch_items AS item
                      ON item."BatchId" = candidate_batch."Id"
                    GROUP BY candidate_batch."Id"
                ) AS item_counts
                WHERE item_counts."BatchId" = batch."Id";
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_instance_version_change_batches_counts",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                sql: "\"TotalItemCount\" >= 0 AND \"TotalItemCount\" <= 10000 AND \"EligibleItemCount\" >= 0 AND \"IneligibleItemCount\" >= 0 AND \"BlockedItemCount\" >= 0 AND \"BlockedItemCount\" <= \"IneligibleItemCount\" AND \"WarningItemCount\" >= 0 AND \"StaleItemCount\" >= 0 AND \"StaleItemCount\" <= \"IneligibleItemCount\" + \"SkippedItemCount\" AND \"QueuedItemCount\" >= 0 AND \"SucceededItemCount\" >= 0 AND \"SkippedItemCount\" >= 0 AND \"FailedItemCount\" >= 0 AND \"CancelledItemCount\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_instance_version_change_batches_counts",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches");

            migrationBuilder.Sql(
                """
                UPDATE flowbit.workflow_instance_version_change_batches AS batch
                SET "StaleItemCount" = item_counts."StaleItemCount"
                FROM (
                    SELECT candidate_batch."Id" AS "BatchId",
                           count(item."Id") FILTER (
                               WHERE item."Status" = 'ineligible'
                                 AND item."ErrorCode" IN (
                                     'stale_since_selection',
                                     'stale_since_preparation',
                                     'stale')
                           )::integer AS "StaleItemCount"
                    FROM flowbit.workflow_instance_version_change_batches AS candidate_batch
                    LEFT JOIN flowbit.workflow_instance_version_change_batch_items AS item
                      ON item."BatchId" = candidate_batch."Id"
                    GROUP BY candidate_batch."Id"
                ) AS item_counts
                WHERE item_counts."BatchId" = batch."Id";
                """);

            migrationBuilder.DropColumn(
                name: "BlockedItemCount",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_instance_version_change_batches_counts",
                schema: "flowbit",
                table: "workflow_instance_version_change_batches",
                sql: "\"TotalItemCount\" >= 0 AND \"TotalItemCount\" <= 10000 AND \"EligibleItemCount\" >= 0 AND \"IneligibleItemCount\" >= 0 AND \"WarningItemCount\" >= 0 AND \"StaleItemCount\" >= 0 AND \"StaleItemCount\" <= \"IneligibleItemCount\" AND \"QueuedItemCount\" >= 0 AND \"SucceededItemCount\" >= 0 AND \"SkippedItemCount\" >= 0 AND \"FailedItemCount\" >= 0 AND \"CancelledItemCount\" >= 0");
        }
    }
}
