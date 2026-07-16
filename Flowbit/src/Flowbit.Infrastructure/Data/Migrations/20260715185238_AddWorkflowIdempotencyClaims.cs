using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowIdempotencyClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "workflow_instances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true,
                collation: "C");

            migrationBuilder.CreateTable(
                name: "workflow_idempotency_claims",
                columns: table => new
                {
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    InstanceId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_idempotency_claims", x => new { x.WorkflowKey, x.IdempotencyKey });
                    table.ForeignKey(
                        name: "FK_workflow_idempotency_claims_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_WorkflowKey_IdempotencyKey",
                table: "workflow_instances",
                columns: new[] { "WorkflowKey", "IdempotencyKey" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_idempotency_claims_InstanceId",
                table: "workflow_idempotency_claims",
                column: "InstanceId",
                unique: true);

            // Historical message starts stored the transport key only as an instance
            // variable named by message.idempotencyVariable. Materialize those owners
            // before activating the instance-to-claim foreign key. This migration is
            // deliberately fail-fast: an incomplete or ambiguous history must be
            // repaired explicitly instead of weakening permanent idempotency.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE _workflow_idempotency_backfill
                (
                    "InstanceId" bigint NOT NULL,
                    "WorkflowKey" character varying(300) NOT NULL,
                    "IdempotencyKey" character varying(300) COLLATE "C" NULL,
                    "ValueType" text NULL,
                    "ConfigurationValid" boolean NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL
                ) ON COMMIT DROP;

                INSERT INTO _workflow_idempotency_backfill
                    ("InstanceId", "WorkflowKey", "IdempotencyKey", "ValueType", "ConfigurationValid", "CreatedAt")
                SELECT
                    instance."Id",
                    instance."WorkflowKey",
                    CASE
                        WHEN jsonb_typeof(variable."ValueJson") = 'string'
                        THEN regexp_replace(
                            variable."ValueJson" #>> '{}',
                            '^[[:space:]]+|[[:space:]]+$',
                            '',
                            'g')
                        ELSE NULL
                    END,
                    jsonb_typeof(variable."ValueJson"),
                    num_nonnulls(
                        NULLIF(btrim(node -> 'idempotency' ->> 'variable'), ''),
                        NULLIF(btrim(node -> 'message' ->> 'idempotencyVariable'), '')) = 1,
                    instance."CreatedAt"
                FROM workflow_instances AS instance
                JOIN workflow_definitions AS definition
                    ON definition."Id" = instance."WorkflowDefinitionId"
                JOIN LATERAL
                (
                    SELECT history."FromStepId"
                    FROM instance_history AS history
                    WHERE history."InstanceId" = instance."Id"
                      AND history."Note" = 'messageStart'
                    ORDER BY history."Id"
                    LIMIT 1
                ) AS origin ON TRUE
                JOIN LATERAL jsonb_array_elements(
                    COALESCE(definition."Definition" -> 'flowNodes', '[]'::jsonb)) AS node
                    ON node ->> 'type' = 'messageStartEvent'
                   AND node ->> 'id' = origin."FromStepId"::text
                LEFT JOIN LATERAL
                (
                    SELECT stored."ValueJson"
                    FROM instance_variables AS stored
                    WHERE stored."InstanceId" = instance."Id"
                      AND lower(stored."VariableName") = lower(COALESCE(
                          NULLIF(btrim(node -> 'idempotency' ->> 'variable'), ''),
                          NULLIF(btrim(node -> 'message' ->> 'idempotencyVariable'), '')))
                    ORDER BY stored."Id"
                    LIMIT 1
                ) AS variable ON TRUE
                WHERE ((node -> 'idempotency') IS NOT NULL
                       AND (node -> 'idempotency') <> 'null'::jsonb)
                   OR ((node -> 'message') ? 'idempotencyVariable'
                       AND (node -> 'message' -> 'idempotencyVariable') <> 'null'::jsonb);

                DO $backfill$
                BEGIN
                    IF EXISTS
                    (
                        SELECT 1
                        FROM _workflow_idempotency_backfill
                        WHERE NOT "ConfigurationValid"
                           OR "ValueType" IS DISTINCT FROM 'string'
                           OR "IdempotencyKey" IS NULL
                           OR "IdempotencyKey" = ''
                           OR char_length("IdempotencyKey") > 300
                    ) THEN
                        RAISE EXCEPTION
                            'Cannot backfill workflow idempotency claims: a historical message start has a missing, malformed, blank, or oversized key.';
                    END IF;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM _workflow_idempotency_backfill
                        GROUP BY "WorkflowKey", "IdempotencyKey"
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION
                            'Cannot backfill workflow idempotency claims: historical keys collide within a workflow family.';
                    END IF;
                END
                $backfill$;

                INSERT INTO workflow_idempotency_claims
                    ("WorkflowKey", "IdempotencyKey", "InstanceId", "CreatedAt")
                SELECT "WorkflowKey", "IdempotencyKey", "InstanceId", "CreatedAt"
                FROM _workflow_idempotency_backfill;

                UPDATE workflow_instances AS instance
                SET "IdempotencyKey" = backfill."IdempotencyKey"
                FROM _workflow_idempotency_backfill AS backfill
                WHERE backfill."InstanceId" = instance."Id";
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_instances_workflow_idempotency_claims_WorkflowKey_~",
                table: "workflow_instances",
                columns: new[] { "WorkflowKey", "IdempotencyKey" },
                principalTable: "workflow_idempotency_claims",
                principalColumns: new[] { "WorkflowKey", "IdempotencyKey" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workflow_instances_workflow_idempotency_claims_WorkflowKey_~",
                table: "workflow_instances");

            migrationBuilder.DropTable(
                name: "workflow_idempotency_claims");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_WorkflowKey_IdempotencyKey",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "workflow_instances");
        }
    }
}
