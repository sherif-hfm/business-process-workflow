using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations;

/// <summary>
/// Repairs databases that applied the original mapping-first version of
/// 20260804155154 before position-first administrative batches were introduced.
/// Some development databases were also created while that historical migration
/// temporarily contained the new shape, so every repair is deliberately
/// conditional and accepts either coherent schema.
/// </summary>
public partial class RepairPositionFirstAdministrativeActionBatches : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE flowbit.sequence_flow_occurrences
                ADD COLUMN IF NOT EXISTS "AdministrativeActionJson" jsonb NULL;

            ALTER TABLE flowbit.sequence_flow_summaries
                ADD COLUMN IF NOT EXISTS "LastActionAdministrativeActionJson" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "LastTraversalAdministrativeActionJson" jsonb NULL;

            DO $flowbit$
            BEGIN
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM pg_catalog.pg_constraint
                    WHERE conname = 'CK_sequence_flow_occurrences_administrative_action'
                      AND conrelid = 'flowbit.sequence_flow_occurrences'::regclass
                ) THEN
                    ALTER TABLE flowbit.sequence_flow_occurrences
                    ADD CONSTRAINT "CK_sequence_flow_occurrences_administrative_action"
                    CHECK
                    (
                        COALESCE
                        (
                            (
                                ("Kind" <> 'administrativeAction'
                                 AND "AdministrativeActionJson" IS NULL)
                                OR
                                (
                                    "Kind" = 'administrativeAction'
                                    AND jsonb_typeof("AdministrativeActionJson") = 'object'
                                    AND jsonb_typeof("AdministrativeActionJson" -> 'batchId') = 'number'
                                    AND ("AdministrativeActionJson" ->> 'batchId')::bigint > 0
                                    AND jsonb_typeof("AdministrativeActionJson" -> 'workflowDefinitionId') = 'number'
                                    AND ("AdministrativeActionJson" ->> 'workflowDefinitionId')::bigint = "WorkflowDefinitionId"
                                    AND jsonb_typeof("AdministrativeActionJson" -> 'flowId') = 'number'
                                    AND ("AdministrativeActionJson" ->> 'flowId')::integer = "SequenceFlowId"
                                    AND
                                    (
                                        (
                                            "AdministrativeActionJson" ->> 'actionKind' = 'directFlow'
                                            AND "AdministrativeActionJson" ->> 'boundaryNodeId' IS NULL
                                            AND "AdministrativeActionJson" ->> 'timerSubscriptionId' IS NULL
                                            AND
                                            (
                                                "AdministrativeActionJson" ->> 'multiInstanceMode' IS NULL
                                                OR "AdministrativeActionJson" ->> 'multiInstanceMode'
                                                    IN ('forceParent', 'completeAllChildren')
                                            )
                                        )
                                        OR
                                        (
                                            "AdministrativeActionJson" ->> 'actionKind' = 'timerBoundary'
                                            AND jsonb_typeof("AdministrativeActionJson" -> 'boundaryNodeId') = 'number'
                                            AND ("AdministrativeActionJson" ->> 'boundaryNodeId')::integer > 0
                                            AND jsonb_typeof("AdministrativeActionJson" -> 'timerSubscriptionId') = 'number'
                                            AND ("AdministrativeActionJson" ->> 'timerSubscriptionId')::bigint > 0
                                            AND "AdministrativeActionJson" ->> 'multiInstanceMode' IS NULL
                                        )
                                    )
                                )
                            ),
                            FALSE
                        )
                    ) NOT VALID;
                END IF;
            END
            $flowbit$;
            """);

        migrationBuilder.Sql("""
            DO $flowbit$
            DECLARE
                has_legacy_shape boolean;
                has_current_shape boolean;
            BEGIN
                SELECT EXISTS
                (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'flowbit'
                      AND table_name = 'administrative_action_batches'
                      AND column_name = 'FlowMappingsJson'
                )
                INTO has_legacy_shape;

                SELECT EXISTS
                (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'flowbit'
                      AND table_name = 'administrative_action_batches'
                      AND column_name = 'ActionKind'
                )
                INTO has_current_shape;

                IF has_legacy_shape AND NOT has_current_shape THEN
                    IF EXISTS
                    (
                        SELECT 1
                        FROM flowbit.administrative_action_batches
                        WHERE "Status" IN ('preparing', 'ready', 'queued', 'running')
                        LIMIT 1
                    ) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '55000',
                            MESSAGE =
                                'Cannot upgrade mapping-first administrative batches while nonterminal batches exist.',
                            DETAIL =
                                'The legacy batch and item rows were left unchanged.',
                            HINT =
                                'Stop the legacy administrative batch workers and finish or cancel every preparing, ready, queued, or running batch before retrying.';
                    END IF;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM flowbit.administrative_action_batch_items AS item
                        JOIN flowbit.administrative_action_batches AS batch
                          ON batch."Id" = item."BatchId"
                        LEFT JOIN flowbit.execution_tokens AS token
                          ON token."Id" = item."TokenId"
                        LEFT JOIN flowbit.user_tasks AS task
                          ON task."Id" = item."UserTaskId"
                        WHERE token."Id" IS NULL
                           OR task."Id" IS NULL
                           OR NOT EXISTS
                              (
                                  SELECT 1
                                  FROM jsonb_array_elements(batch."FlowMappingsJson") AS mapping(value)
                                  WHERE (mapping.value ->> 'workflowDefinitionId')::bigint =
                                            item."WorkflowDefinitionId"
                                    AND (mapping.value ->> 'flowId')::integer = item."FlowId"
                                    AND (mapping.value ->> 'sourceNodeId')::integer = task."NodeId"
                              )
                        LIMIT 1
                    ) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '55000',
                            MESSAGE =
                                'Cannot upgrade inconsistent mapping-first administrative batch items.',
                            DETAIL =
                                'An item does not match its frozen definition, flow, source task, or token.',
                            HINT =
                                'Repair or archive the inconsistent legacy audit row before retrying this migration.';
                    END IF;

                    ALTER TABLE flowbit.administrative_action_batches
                        DROP CONSTRAINT IF EXISTS "CK_administrative_action_batches_flow_mappings",
                        DROP CONSTRAINT IF EXISTS "CK_administrative_action_batches_counts";

                    ALTER TABLE flowbit.administrative_action_batches
                        ADD COLUMN "WorkflowDefinitionId" bigint NULL,
                        ADD COLUMN "SourceNodeId" integer NULL,
                        ADD COLUMN "ActionKind" character varying(32) NULL,
                        ADD COLUMN "FlowId" integer NULL,
                        ADD COLUMN "BoundaryNodeId" integer NULL,
                        ADD COLUMN "MultiInstanceMode" character varying(32) NULL,
                        ADD COLUMN "ActionSnapshotJson" jsonb NULL,
                        ADD COLUMN "TotalAffectedTaskCount" integer NULL;

                    UPDATE flowbit.administrative_action_batches AS batch
                    SET
                        "WorkflowDefinitionId" =
                            (batch."FlowMappingsJson" -> 0 ->> 'workflowDefinitionId')::bigint,
                        "SourceNodeId" =
                            (batch."FlowMappingsJson" -> 0 ->> 'sourceNodeId')::integer,
                        "ActionKind" = 'directFlow',
                        "FlowId" =
                            (batch."FlowMappingsJson" -> 0 ->> 'flowId')::integer,
                        "ActionSnapshotJson" = jsonb_build_object
                        (
                            'workflowDefinitionId',
                                (batch."FlowMappingsJson" -> 0 ->> 'workflowDefinitionId')::bigint,
                            'workflowVersion',
                                (batch."FlowMappingsJson" -> 0 ->> 'workflowVersion')::integer,
                            'actionKind', 'directFlow',
                            'flowId',
                                (batch."FlowMappingsJson" -> 0 ->> 'flowId')::integer,
                            'flowExternalId', batch."FlowMappingsJson" -> 0 -> 'flowExternalId',
                            'flowName', COALESCE(
                                batch."FlowMappingsJson" -> 0 -> 'flowName',
                                '"Legacy administrative action"'::jsonb),
                            'sourceNodeId',
                                (batch."FlowMappingsJson" -> 0 ->> 'sourceNodeId')::integer,
                            'sourceNodeName', COALESCE(
                                batch."FlowMappingsJson" -> 0 -> 'sourceNodeName',
                                '"Legacy source"'::jsonb),
                            'targetNodeId',
                                (batch."FlowMappingsJson" -> 0 ->> 'targetNodeId')::integer,
                            'targetNodeName', COALESCE(
                                batch."FlowMappingsJson" -> 0 -> 'targetNodeName',
                                '"Legacy target"'::jsonb),
                            'targetNodeType', 'userTask',
                            'condition', NULL,
                            'roles', COALESCE(
                                batch."FlowMappingsJson" -> 0 -> 'roles', '[]'::jsonb),
                            'variables', COALESCE(
                                batch."FlowMappingsJson" -> 0 -> 'variables', '[]'::jsonb),
                            'boundaryNodeId', NULL,
                            'boundaryNodeName', NULL,
                            'timer', NULL,
                            'authoredCancelActivity', NULL,
                            'legacyMappingFirst', jsonb_build_object
                            (
                                'flowMappings', batch."FlowMappingsJson"
                            )
                        ),
                        "TotalAffectedTaskCount" =
                        (
                            SELECT count(*)::integer
                            FROM flowbit.administrative_action_batch_items AS item
                            WHERE item."BatchId" = batch."Id"
                        );

                    ALTER TABLE flowbit.administrative_action_batches
                        ALTER COLUMN "WorkflowDefinitionId" SET NOT NULL,
                        ALTER COLUMN "SourceNodeId" SET NOT NULL,
                        ALTER COLUMN "ActionKind" SET NOT NULL,
                        ALTER COLUMN "FlowId" SET NOT NULL,
                        ALTER COLUMN "ActionSnapshotJson" SET NOT NULL,
                        ALTER COLUMN "ActionSnapshotJson" SET DEFAULT '{}'::jsonb,
                        ALTER COLUMN "TotalAffectedTaskCount" SET NOT NULL,
                        ALTER COLUMN "Reason" DROP NOT NULL;

                    ALTER TABLE flowbit.administrative_action_batch_items
                        DROP CONSTRAINT IF EXISTS "FK_administrative_action_batch_items_user_tasks_NewUserTaskId";

                    DROP INDEX IF EXISTS flowbit."IX_administrative_action_batch_items_NewUserTaskId";
                    DROP INDEX IF EXISTS flowbit."IX_administrative_action_batch_items_BatchId_UserTaskId";

                    ALTER TABLE flowbit.administrative_action_batch_items
                        ADD COLUMN "PositionKind" character varying(32) NULL,
                        ADD COLUMN "MultiInstanceExecutionId" bigint NULL,
                        ADD COLUMN "TokenActivationId" uuid NULL,
                        ADD COLUMN "SourceNodeId" integer NULL,
                        ADD COLUMN "CapturedPositionUpdatedAt" timestamp with time zone NULL,
                        ADD COLUMN "TimerSubscriptionId" bigint NULL,
                        ADD COLUMN "TimerJobId" bigint NULL,
                        ADD COLUMN "CapturedTimerOccurrence" bigint NULL,
                        ADD COLUMN "CapturedTimerStatus" character varying(32) NULL,
                        ADD COLUMN "CapturedTimerSubscriptionUpdatedAt" timestamp with time zone NULL,
                        ADD COLUMN "AffectedTaskCount" integer NULL;

                    UPDATE flowbit.administrative_action_batch_items AS item
                    SET
                        "PositionKind" = 'userTask',
                        "TokenActivationId" = '00000000-0000-0000-0000-000000000000'::uuid,
                        "SourceNodeId" = task."NodeId",
                        "CapturedPositionUpdatedAt" = item."CapturedUserTaskUpdatedAt",
                        "AffectedTaskCount" = 1,
                        "ResultJson" = jsonb_build_object
                        (
                            'legacyOriginalResult', item."ResultJson",
                            'legacyMappingFirst',
                            jsonb_build_object
                            (
                                'capturedInstanceUpdatedAt', item."CapturedInstanceUpdatedAt",
                                'newUserTaskId', item."NewUserTaskId",
                                'tokenActivationUnavailable', TRUE,
                                'observedCurrentTokenActivationId', token."ActivationId"
                            )
                        )
                    FROM flowbit.execution_tokens AS token,
                         flowbit.user_tasks AS task
                    WHERE token."Id" = item."TokenId"
                      AND task."Id" = item."UserTaskId";

                    UPDATE flowbit.administrative_action_batch_items
                    SET
                        "Status" = 'cancelled',
                        "UpdatedAt" = CURRENT_TIMESTAMP,
                        "CompletedAt" = COALESCE("CompletedAt", CURRENT_TIMESTAMP),
                        "ErrorCode" = COALESCE("ErrorCode", 'legacy_batch_retired'),
                        "ErrorDescription" = COALESCE
                        (
                            "ErrorDescription",
                            'This mapping-first batch was retired during the position-first schema upgrade.'
                        )
                    WHERE "Status" IN ('preparing', 'eligible', 'queued');

                    ALTER TABLE flowbit.administrative_action_batch_items
                        ALTER COLUMN "PositionKind" SET NOT NULL,
                        ALTER COLUMN "UserTaskId" DROP NOT NULL,
                        ALTER COLUMN "TokenActivationId" SET NOT NULL,
                        ALTER COLUMN "SourceNodeId" SET NOT NULL,
                        ALTER COLUMN "CapturedPositionUpdatedAt" SET NOT NULL,
                        ALTER COLUMN "AffectedTaskCount" SET NOT NULL,
                        DROP COLUMN "CapturedInstanceUpdatedAt",
                        DROP COLUMN "CapturedUserTaskUpdatedAt",
                        DROP COLUMN "NewUserTaskId";

                    UPDATE flowbit.administrative_action_batches
                    SET
                        "Status" = 'cancelled',
                        "CancelledBy" = COALESCE("CancelledBy", 'schema-migration'),
                        "CancellationReason" = COALESCE
                        (
                            "CancellationReason",
                            'Mapping-first batch retired by the position-first schema upgrade.'
                        ),
                        "CancelledAt" = COALESCE("CancelledAt", CURRENT_TIMESTAMP),
                        "CompletedAt" = COALESCE("CompletedAt", CURRENT_TIMESTAMP),
                        "UpdatedAt" = CURRENT_TIMESTAMP
                    WHERE "Status" IN ('preparing', 'ready', 'queued', 'running');

                    UPDATE flowbit.administrative_action_batches AS batch
                    SET
                        "TotalItemCount" = counts.total_count,
                        "TotalAffectedTaskCount" = counts.total_count,
                        "EligibleItemCount" = counts.eligible_count,
                        "IneligibleItemCount" = counts.ineligible_count,
                        "QueuedItemCount" = counts.queued_count,
                        "SucceededItemCount" = counts.succeeded_count,
                        "SkippedItemCount" = counts.skipped_count,
                        "FailedItemCount" = counts.failed_count,
                        "CancelledItemCount" = counts.cancelled_count
                    FROM
                    (
                        SELECT
                            candidate."Id" AS batch_id,
                            count(item."Id")::integer AS total_count,
                            count(*) FILTER (WHERE item."Status" = 'eligible')::integer AS eligible_count,
                            count(*) FILTER (WHERE item."Status" = 'ineligible')::integer AS ineligible_count,
                            count(*) FILTER (WHERE item."Status" = 'queued')::integer AS queued_count,
                            count(*) FILTER (WHERE item."Status" = 'succeeded')::integer AS succeeded_count,
                            count(*) FILTER (WHERE item."Status" = 'skipped')::integer AS skipped_count,
                            count(*) FILTER (WHERE item."Status" = 'failed')::integer AS failed_count,
                            count(*) FILTER (WHERE item."Status" = 'cancelled')::integer AS cancelled_count
                        FROM flowbit.administrative_action_batches AS candidate
                        LEFT JOIN flowbit.administrative_action_batch_items AS item
                          ON item."BatchId" = candidate."Id"
                        GROUP BY candidate."Id"
                    ) AS counts
                    WHERE counts.batch_id = batch."Id";

                    ALTER TABLE flowbit.administrative_action_batches
                        DROP COLUMN "FlowMappingsJson";

                    ALTER TABLE flowbit.administrative_action_batches
                        ADD CONSTRAINT "CK_administrative_action_batches_action"
                            CHECK
                            (
                                "WorkflowDefinitionId" > 0
                                AND "SourceNodeId" > 0
                                AND "FlowId" > 0
                                AND
                                (
                                    ("ActionKind" = 'directFlow' AND "BoundaryNodeId" IS NULL)
                                    OR
                                    ("ActionKind" = 'timerBoundary'
                                     AND "BoundaryNodeId" > 0
                                     AND "MultiInstanceMode" IS NULL)
                                )
                                AND
                                (
                                    "MultiInstanceMode" IS NULL
                                    OR "MultiInstanceMode" IN ('forceParent', 'completeAllChildren')
                                )
                            ),
                        ADD CONSTRAINT "CK_administrative_action_batches_action_snapshot"
                            CHECK (jsonb_typeof("ActionSnapshotJson") = 'object'),
                        ADD CONSTRAINT "CK_administrative_action_batches_counts"
                            CHECK
                            (
                                "TotalItemCount" >= 0
                                AND "TotalItemCount" <= 10000
                                AND "EligibleItemCount" >= 0
                                AND "IneligibleItemCount" >= 0
                                AND "QueuedItemCount" >= 0
                                AND "SucceededItemCount" >= 0
                                AND "SkippedItemCount" >= 0
                                AND "FailedItemCount" >= 0
                                AND "CancelledItemCount" >= 0
                                AND "TotalAffectedTaskCount" >= 0
                            ),
                        ADD CONSTRAINT "FK_administrative_action_batches_workflow_definitions_WorkflowDefinitionId"
                            FOREIGN KEY ("WorkflowDefinitionId")
                            REFERENCES flowbit.workflow_definitions ("Id")
                            ON DELETE RESTRICT;

                    ALTER TABLE flowbit.administrative_action_batch_items
                        ADD CONSTRAINT "CK_administrative_action_batch_items_position"
                            CHECK
                            (
                                (
                                    ("PositionKind" = 'userTask'
                                     AND "UserTaskId" IS NOT NULL
                                     AND "MultiInstanceExecutionId" IS NULL)
                                    OR
                                    ("PositionKind" = 'multiInstanceExecution'
                                     AND "UserTaskId" IS NULL
                                     AND "MultiInstanceExecutionId" IS NOT NULL)
                                )
                                AND "SourceNodeId" > 0
                                AND "FlowId" > 0
                                AND "AffectedTaskCount" >= 0
                                AND "AffectedTaskCount" <= 10000
                            ),
                        ADD CONSTRAINT "CK_administrative_action_batch_items_timer_fence"
                            CHECK
                            (
                                (
                                    "TimerSubscriptionId" IS NULL
                                    AND "TimerJobId" IS NULL
                                    AND "CapturedTimerOccurrence" IS NULL
                                    AND "CapturedTimerStatus" IS NULL
                                    AND "CapturedTimerSubscriptionUpdatedAt" IS NULL
                                )
                                OR
                                (
                                    "TimerSubscriptionId" IS NOT NULL
                                    AND "CapturedTimerOccurrence" IS NOT NULL
                                    AND "CapturedTimerStatus" IN ('active', 'paused', 'completed', 'cancelled')
                                    AND "CapturedTimerSubscriptionUpdatedAt" IS NOT NULL
                                )
                            ),
                        ADD CONSTRAINT "FK_administrative_action_batch_items_multi_instance_executions_MultiInstanceExecutionId"
                            FOREIGN KEY ("MultiInstanceExecutionId")
                            REFERENCES flowbit.multi_instance_executions ("Id")
                            ON DELETE RESTRICT,
                        ADD CONSTRAINT "FK_administrative_action_batch_items_timer_subscriptions_TimerSubscriptionId"
                            FOREIGN KEY ("TimerSubscriptionId")
                            REFERENCES flowbit.timer_subscriptions ("Id")
                            ON DELETE RESTRICT;

                    CREATE INDEX "IX_administrative_action_batches_WorkflowDefinitionId"
                        ON flowbit.administrative_action_batches ("WorkflowDefinitionId");
                    CREATE INDEX "IX_administrative_action_batches_WorkflowDefinitionId_SourceNodeId_ActionKind_FlowId_UpdatedAt_Id"
                        ON flowbit.administrative_action_batches
                        ("WorkflowDefinitionId", "SourceNodeId", "ActionKind", "FlowId", "UpdatedAt", "Id");

                    CREATE UNIQUE INDEX "IX_administrative_action_batch_items_BatchId_UserTaskId"
                        ON flowbit.administrative_action_batch_items ("BatchId", "UserTaskId")
                        WHERE "UserTaskId" IS NOT NULL;
                    CREATE UNIQUE INDEX "IX_administrative_action_batch_items_BatchId_MultiInstanceExecutionId"
                        ON flowbit.administrative_action_batch_items ("BatchId", "MultiInstanceExecutionId")
                        WHERE "MultiInstanceExecutionId" IS NOT NULL;
                    CREATE INDEX "IX_administrative_action_batch_items_MultiInstanceExecutionId"
                        ON flowbit.administrative_action_batch_items ("MultiInstanceExecutionId");
                    CREATE INDEX "IX_administrative_action_batch_items_TimerSubscriptionId"
                        ON flowbit.administrative_action_batch_items ("TimerSubscriptionId");
                ELSIF has_current_shape AND NOT has_legacy_shape THEN
                    IF EXISTS
                    (
                        SELECT required.column_name
                        FROM
                        (
                            VALUES
                                ('WorkflowDefinitionId'),
                                ('SourceNodeId'),
                                ('ActionKind'),
                                ('FlowId'),
                                ('BoundaryNodeId'),
                                ('MultiInstanceMode'),
                                ('ActionSnapshotJson'),
                                ('TotalAffectedTaskCount')
                        ) AS required(column_name)
                        WHERE NOT EXISTS
                        (
                            SELECT 1
                            FROM information_schema.columns AS actual
                            WHERE actual.table_schema = 'flowbit'
                              AND actual.table_name = 'administrative_action_batches'
                              AND actual.column_name = required.column_name
                        )
                    )
                    OR EXISTS
                    (
                        SELECT required.column_name
                        FROM
                        (
                            VALUES
                                ('PositionKind'),
                                ('UserTaskId'),
                                ('MultiInstanceExecutionId'),
                                ('TokenActivationId'),
                                ('WorkflowDefinitionId'),
                                ('SourceNodeId'),
                                ('FlowId'),
                                ('CapturedPositionUpdatedAt'),
                                ('TimerSubscriptionId'),
                                ('TimerJobId'),
                                ('CapturedTimerOccurrence'),
                                ('CapturedTimerStatus'),
                                ('CapturedTimerSubscriptionUpdatedAt'),
                                ('AffectedTaskCount')
                        ) AS required(column_name)
                        WHERE NOT EXISTS
                        (
                            SELECT 1
                            FROM information_schema.columns AS actual
                            WHERE actual.table_schema = 'flowbit'
                              AND actual.table_name = 'administrative_action_batch_items'
                              AND actual.column_name = required.column_name
                        )
                    ) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '55000',
                            MESSAGE =
                                'The administrative batch schema is partially upgraded.',
                            HINT =
                                'Restore the schema from a known migration state before rerunning this repair.';
                    END IF;
                ELSE
                    RAISE EXCEPTION USING
                        ERRCODE = '55000',
                        MESSAGE =
                            'The administrative batch schema is neither the supported legacy shape nor the current position-first shape.',
                        HINT =
                            'Restore the schema from a known migration state before rerunning this repair.';
                END IF;
            END
            $flowbit$;
            """);

        migrationBuilder.Sql("""
            DO $flowbit$
            BEGIN
                IF EXISTS
                (
                    SELECT 1
                    FROM flowbit.sequence_flow_occurrences AS occurrence
                    LEFT JOIN flowbit.user_tasks AS task
                      ON task."Id" = occurrence."UserTaskId"
                    LEFT JOIN flowbit.administrative_action_batches AS batch
                      ON batch."Id" = task."AdministrativeActionBatchId"
                    WHERE occurrence."Kind" = 'administrativeAction'
                      AND occurrence."AdministrativeActionJson" IS NULL
                      AND
                      (
                          batch."Id" IS NULL
                          OR NOT EXISTS
                             (
                                 SELECT 1
                                 FROM jsonb_array_elements
                                 (
                                     batch."ActionSnapshotJson"
                                         -> 'legacyMappingFirst'
                                         -> 'flowMappings'
                                 ) AS mapping(value)
                                 WHERE (mapping.value ->> 'workflowDefinitionId')::bigint =
                                           occurrence."WorkflowDefinitionId"
                                   AND (mapping.value ->> 'flowId')::integer =
                                           occurrence."SequenceFlowId"
                             )
                      )
                    LIMIT 1
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '55000',
                        MESSAGE =
                            'Cannot correlate legacy administrative sequence-flow evidence to a migrated batch.',
                        DETAIL =
                            'The legacy evidence and batch audit rows were left unchanged.',
                        HINT =
                            'Repair the orphaned user-task or batch correlation before retrying this migration.';
                END IF;

                UPDATE flowbit.sequence_flow_occurrences AS occurrence
                SET "AdministrativeActionJson" = jsonb_strip_nulls
                (
                    jsonb_build_object
                    (
                        'batchId', batch."Id",
                        'workflowDefinitionId', occurrence."WorkflowDefinitionId",
                        'actionKind', 'directFlow',
                        'flowId', occurrence."SequenceFlowId",
                        'boundaryNodeId', NULL,
                        'timerSubscriptionId', NULL,
                        'multiInstanceMode', NULL,
                        'reason', batch."Reason"
                    )
                )
                FROM flowbit.user_tasks AS task
                JOIN flowbit.administrative_action_batches AS batch
                  ON batch."Id" = task."AdministrativeActionBatchId"
                WHERE occurrence."Kind" = 'administrativeAction'
                  AND occurrence."AdministrativeActionJson" IS NULL
                  AND task."Id" = occurrence."UserTaskId";

                UPDATE flowbit.sequence_flow_summaries AS summary
                SET "LastActionAdministrativeActionJson" =
                (
                    SELECT occurrence."AdministrativeActionJson"
                    FROM flowbit.sequence_flow_occurrences AS occurrence
                    WHERE occurrence."InstanceId" = summary."InstanceId"
                      AND occurrence."SequenceFlowId" = summary."SequenceFlowId"
                      AND occurrence."IsAction"
                      AND occurrence."Kind" = 'administrativeAction'
                    ORDER BY occurrence."OccurredAt" DESC, occurrence."Id" DESC
                    LIMIT 1
                )
                WHERE summary."LastActionKind" = 'administrativeAction'
                  AND summary."LastActionAdministrativeActionJson" IS NULL;

                UPDATE flowbit.sequence_flow_summaries AS summary
                SET "LastTraversalAdministrativeActionJson" =
                (
                    SELECT occurrence."AdministrativeActionJson"
                    FROM flowbit.sequence_flow_occurrences AS occurrence
                    WHERE occurrence."InstanceId" = summary."InstanceId"
                      AND occurrence."SequenceFlowId" = summary."SequenceFlowId"
                      AND occurrence."IsTraversal"
                      AND occurrence."Kind" = 'administrativeAction'
                    ORDER BY occurrence."OccurredAt" DESC, occurrence."Id" DESC
                    LIMIT 1
                )
                WHERE summary."LastTraversalKind" = 'administrativeAction'
                  AND summary."LastTraversalAdministrativeActionJson" IS NULL;
            END
            $flowbit$;

            ALTER TABLE flowbit.sequence_flow_occurrences
                VALIDATE CONSTRAINT "CK_sequence_flow_occurrences_administrative_action";
            """);

        migrationBuilder.Sql("""
            WITH legacy_value AS
            (
                SELECT COALESCE
                (
                    (
                        SELECT "Value"
                        FROM flowbit.engine_settings
                        WHERE
                            ("Namespace" = 'WorkflowBatchActions' AND "Key" = 'MaxItems')
                            OR
                            (BTRIM(COALESCE("Namespace", '')) = ''
                             AND "Key" = 'WorkflowBatchActions.MaxItems')
                        ORDER BY "Id"
                        LIMIT 1
                    ),
                    '10000'
                ) AS value
            )
            INSERT INTO flowbit.engine_settings
                ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
            SELECT
                'WorkflowBatchActions',
                'MaxAffectedTasks',
                CASE
                    WHEN value ~ '^[0-9]+$' THEN
                        CASE
                            WHEN value::numeric BETWEEN 1 AND 10000 THEN value
                            ELSE '10000'
                        END
                    ELSE '10000'
                END,
                'Maximum total number of ordinary or multi-instance user tasks affected by one administrative action batch. Invalid or missing values default to 10000.',
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            FROM legacy_value
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM flowbit.engine_settings
                WHERE
                    ("Namespace" = 'WorkflowBatchActions' AND "Key" = 'MaxAffectedTasks')
                    OR
                    (BTRIM(COALESCE("Namespace", '')) = ''
                     AND "Key" = 'WorkflowBatchActions.MaxAffectedTasks')
            )
            ON CONFLICT ("Namespace", "Key") DO NOTHING;

            DELETE FROM flowbit.engine_settings
            WHERE
                ("Namespace" = 'WorkflowBatchActions'
                 AND "Key" IN ('RequiredRole', 'MaxItems'))
                OR
                (BTRIM(COALESCE("Namespace", '')) = ''
                 AND "Key" IN
                 (
                     'WorkflowBatchActions.RequiredRole',
                     'WorkflowBatchActions.MaxItems'
                 ));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $flowbit$
            BEGIN
                IF EXISTS
                (
                    SELECT 1
                    FROM flowbit.administrative_action_batches
                    LIMIT 1
                )
                OR EXISTS
                (
                    SELECT 1
                    FROM flowbit.administrative_action_batch_items
                    LIMIT 1
                ) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '55000',
                        MESSAGE =
                            'Cannot downgrade position-first administrative batches while audit rows exist.',
                        DETAIL =
                            'The administrative batch and item rows were left unchanged.',
                        HINT =
                            'Retain this migration or archive the position-first audit data before downgrading.';
                END IF;

                ALTER TABLE flowbit.administrative_action_batch_items
                    DROP CONSTRAINT IF EXISTS "CK_administrative_action_batch_items_position",
                    DROP CONSTRAINT IF EXISTS "CK_administrative_action_batch_items_timer_fence",
                    DROP CONSTRAINT IF EXISTS "FK_administrative_action_batch_items_multi_instance_executions_MultiInstanceExecutionId",
                    DROP CONSTRAINT IF EXISTS "FK_administrative_action_batch_items_multi_instance_executions~",
                    DROP CONSTRAINT IF EXISTS "FK_administrative_action_batch_items_timer_subscriptions_TimerSubscriptionId",
                    DROP CONSTRAINT IF EXISTS "FK_administrative_action_batch_items_timer_subscriptions_Timer~";

                ALTER TABLE flowbit.administrative_action_batches
                    DROP CONSTRAINT IF EXISTS "CK_administrative_action_batches_action",
                    DROP CONSTRAINT IF EXISTS "CK_administrative_action_batches_action_snapshot",
                    DROP CONSTRAINT IF EXISTS "CK_administrative_action_batches_counts",
                    DROP CONSTRAINT IF EXISTS "FK_administrative_action_batches_workflow_definitions_WorkflowDefinitionId",
                    DROP CONSTRAINT IF EXISTS "FK_administrative_action_batches_workflow_definitions_Workflow~";

                DROP INDEX IF EXISTS flowbit."IX_administrative_action_batches_WorkflowDefinitionId";
                DROP INDEX IF EXISTS flowbit."IX_administrative_action_batches_WorkflowDefinitionId_SourceNodeId_ActionKind_FlowId_UpdatedAt_Id";
                DROP INDEX IF EXISTS flowbit."IX_administrative_action_batches_WorkflowDefinitionId_SourceNo~";
                DROP INDEX IF EXISTS flowbit."IX_administrative_action_batch_items_BatchId_UserTaskId";
                DROP INDEX IF EXISTS flowbit."IX_administrative_action_batch_items_BatchId_MultiInstanceExecutionId";
                DROP INDEX IF EXISTS flowbit."IX_administrative_action_batch_items_BatchId_MultiInstanceExec~";
                DROP INDEX IF EXISTS flowbit."IX_administrative_action_batch_items_MultiInstanceExecutionId";
                DROP INDEX IF EXISTS flowbit."IX_administrative_action_batch_items_TimerSubscriptionId";

                ALTER TABLE flowbit.administrative_action_batches
                    ADD COLUMN "FlowMappingsJson" jsonb NULL;

                ALTER TABLE flowbit.administrative_action_batches
                    ALTER COLUMN "FlowMappingsJson" SET NOT NULL,
                    ALTER COLUMN "Reason" SET NOT NULL,
                    DROP COLUMN "WorkflowDefinitionId",
                    DROP COLUMN "SourceNodeId",
                    DROP COLUMN "ActionKind",
                    DROP COLUMN "FlowId",
                    DROP COLUMN "BoundaryNodeId",
                    DROP COLUMN "MultiInstanceMode",
                    DROP COLUMN "ActionSnapshotJson",
                    DROP COLUMN "TotalAffectedTaskCount";

                ALTER TABLE flowbit.administrative_action_batches
                    ADD CONSTRAINT "CK_administrative_action_batches_counts"
                        CHECK
                        (
                            "TotalItemCount" >= 0
                            AND "TotalItemCount" <= 10000
                            AND "EligibleItemCount" >= 0
                            AND "IneligibleItemCount" >= 0
                            AND "QueuedItemCount" >= 0
                            AND "SucceededItemCount" >= 0
                            AND "SkippedItemCount" >= 0
                            AND "FailedItemCount" >= 0
                            AND "CancelledItemCount" >= 0
                        ),
                    ADD CONSTRAINT "CK_administrative_action_batches_flow_mappings"
                        CHECK
                        (
                            jsonb_typeof("FlowMappingsJson") = 'array'
                            AND jsonb_array_length("FlowMappingsJson") > 0
                        );

                ALTER TABLE flowbit.administrative_action_batch_items
                    ADD COLUMN "CapturedInstanceUpdatedAt" timestamp with time zone NULL,
                    ADD COLUMN "CapturedUserTaskUpdatedAt" timestamp with time zone NULL,
                    ADD COLUMN "NewUserTaskId" bigint NULL;

                ALTER TABLE flowbit.administrative_action_batch_items
                    ALTER COLUMN "UserTaskId" SET NOT NULL,
                    ALTER COLUMN "CapturedInstanceUpdatedAt" SET NOT NULL,
                    ALTER COLUMN "CapturedUserTaskUpdatedAt" SET NOT NULL,
                    DROP COLUMN "PositionKind",
                    DROP COLUMN "MultiInstanceExecutionId",
                    DROP COLUMN "TokenActivationId",
                    DROP COLUMN "SourceNodeId",
                    DROP COLUMN "CapturedPositionUpdatedAt",
                    DROP COLUMN "TimerSubscriptionId",
                    DROP COLUMN "TimerJobId",
                    DROP COLUMN "CapturedTimerOccurrence",
                    DROP COLUMN "CapturedTimerStatus",
                    DROP COLUMN "CapturedTimerSubscriptionUpdatedAt",
                    DROP COLUMN "AffectedTaskCount";

                ALTER TABLE flowbit.administrative_action_batch_items
                    ADD CONSTRAINT "FK_administrative_action_batch_items_user_tasks_NewUserTaskId"
                        FOREIGN KEY ("NewUserTaskId")
                        REFERENCES flowbit.user_tasks ("Id")
                        ON DELETE RESTRICT;

                CREATE UNIQUE INDEX "IX_administrative_action_batch_items_BatchId_UserTaskId"
                    ON flowbit.administrative_action_batch_items ("BatchId", "UserTaskId");
                CREATE INDEX "IX_administrative_action_batch_items_NewUserTaskId"
                    ON flowbit.administrative_action_batch_items ("NewUserTaskId");
            END
            $flowbit$;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE flowbit.sequence_flow_occurrences
                DROP CONSTRAINT IF EXISTS "CK_sequence_flow_occurrences_administrative_action";

            ALTER TABLE flowbit.sequence_flow_occurrences
                DROP COLUMN IF EXISTS "AdministrativeActionJson";

            ALTER TABLE flowbit.sequence_flow_summaries
                DROP COLUMN IF EXISTS "LastActionAdministrativeActionJson",
                DROP COLUMN IF EXISTS "LastTraversalAdministrativeActionJson";

            DELETE FROM flowbit.engine_settings
            WHERE
                ("Namespace" = 'WorkflowBatchActions' AND "Key" = 'MaxAffectedTasks')
                OR
                (BTRIM(COALESCE("Namespace", '')) = ''
                 AND "Key" = 'WorkflowBatchActions.MaxAffectedTasks');

            INSERT INTO flowbit.engine_settings
                ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
            VALUES
                (
                    'WorkflowBatchActions',
                    'RequiredRole',
                    'admin',
                    'Comma-separated roles required to prepare, confirm, cancel, and monitor administrative action batches. Missing or blank values default to admin.',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                ),
                (
                    'WorkflowBatchActions',
                    'MaxItems',
                    '10000',
                    'Maximum number of frozen user tasks allowed in one administrative action batch. Invalid or missing values default to 10000.',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                )
            ON CONFLICT ("Namespace", "Key") DO NOTHING;
            """);
    }
}
