using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionBatchMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration =
        "20260803175244_AddSettingDescriptionsAndManagementRole";
    private const string LegacyBatchMigration =
        "20260804155154_AddAdministrativeActionBatches";
    private const string TargetMigration =
        "20260808120000_RepairPositionFirstAdministrativeActionBatches";

    [Fact]
    public async Task MigrationCreatesBatchSchemaAndSeedsCanonicalSettings()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using (var command = new NpgsqlCommand("""
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = 'flowbit'
                  AND table_name IN (
                      'administrative_action_batches',
                      'administrative_action_batch_items')
                ORDER BY table_name
                """, connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                var tables = new List<string>();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
                Assert.Equal(
                    ["administrative_action_batch_items", "administrative_action_batches"],
                    tables);
            }

            await using (var command = new NpgsqlCommand("""
                SELECT table_name || '.' || column_name
                FROM information_schema.columns
                WHERE table_schema = 'flowbit'
                  AND (
                      (table_name = 'user_tasks'
                       AND column_name IN (
                           'AdministrativeActionBatchId',
                           'CompletionKind',
                           'CompletionReason'))
                      OR
                      (table_name = 'instance_history'
                       AND column_name IN ('AdministrativeActionBatchId', 'Reason'))
                      OR
                      (table_name = 'workflow_instance_version_changes'
                       AND column_name = 'AdministrativeActionBatchId')
                      )
                ORDER BY 1
                """, connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                var columns = new List<string>();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(0));
                }
                Assert.Equal(
                    [
                        "instance_history.AdministrativeActionBatchId",
                        "instance_history.Reason",
                        "user_tasks.AdministrativeActionBatchId",
                        "user_tasks.CompletionKind",
                        "user_tasks.CompletionReason"
                    ],
                    columns);
            }

            await using (var command = new NpgsqlCommand("""
                SELECT table_name || '.' || column_name
                FROM information_schema.columns
                WHERE table_schema = 'flowbit'
                  AND (
                      (table_name = 'administrative_action_batches'
                       AND column_name IN (
                           'FlowMappingsJson',
                           'WorkflowDefinitionId',
                           'SourceNodeId',
                           'ActionKind',
                           'FlowId',
                           'BoundaryNodeId',
                           'MultiInstanceMode',
                           'ActionSnapshotJson'))
                      OR
                      (table_name = 'administrative_action_batch_items'
                       AND column_name IN (
                           'PositionKind',
                           'UserTaskId',
                           'MultiInstanceExecutionId',
                           'TokenActivationId',
                           'WorkflowDefinitionId',
                           'SourceNodeId',
                           'FlowId',
                           'TimerSubscriptionId',
                           'TimerJobId',
                           'CapturedTimerOccurrence',
                           'CapturedTimerStatus',
                           'CapturedTimerSubscriptionUpdatedAt',
                           'AffectedTaskCount')))
                ORDER BY 1
                """, connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                var columns = new List<string>();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(0));
                }
                Assert.Equal(
                    [
                        "administrative_action_batch_items.AffectedTaskCount",
                        "administrative_action_batch_items.CapturedTimerOccurrence",
                        "administrative_action_batch_items.CapturedTimerStatus",
                        "administrative_action_batch_items.CapturedTimerSubscriptionUpdatedAt",
                        "administrative_action_batch_items.FlowId",
                        "administrative_action_batch_items.MultiInstanceExecutionId",
                        "administrative_action_batch_items.PositionKind",
                        "administrative_action_batch_items.SourceNodeId",
                        "administrative_action_batch_items.TimerJobId",
                        "administrative_action_batch_items.TimerSubscriptionId",
                        "administrative_action_batch_items.TokenActivationId",
                        "administrative_action_batch_items.UserTaskId",
                        "administrative_action_batch_items.WorkflowDefinitionId",
                        "administrative_action_batches.ActionKind",
                        "administrative_action_batches.ActionSnapshotJson",
                        "administrative_action_batches.BoundaryNodeId",
                        "administrative_action_batches.FlowId",
                        "administrative_action_batches.MultiInstanceMode",
                        "administrative_action_batches.SourceNodeId",
                        "administrative_action_batches.WorkflowDefinitionId"
                    ],
                    columns);
            }

            await using (var command = new NpgsqlCommand("""
                SELECT "Namespace" || '.' || "Key", "Value", "Description"
                FROM flowbit.engine_settings
                WHERE ("Namespace", "Key") IN (
                    ('WorkflowBatchActions', 'RequiredRole'),
                    ('WorkflowBatchActions', 'MaxItems'),
                    ('WorkflowBatchActions', 'MaxAffectedTasks'))
                ORDER BY 1
                """, connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                var settings = new List<(string Key, string Value, string? Description)>();
                while (await reader.ReadAsync())
                {
                    settings.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
                var setting = Assert.Single(settings);
                Assert.Equal("WorkflowBatchActions.MaxAffectedTasks", setting.Key);
                Assert.Equal("10000", setting.Value);
                Assert.False(string.IsNullOrWhiteSpace(setting.Description));
            }

            await using (var command = new NpgsqlCommand("""
                SELECT pg_get_constraintdef(oid)
                FROM pg_catalog.pg_constraint
                WHERE conname = 'CK_node_executions_completion_reason'
                  AND conrelid = 'flowbit.node_executions'::regclass
                """, connection))
            {
                var definition = Assert.IsType<string>(
                    await command.ExecuteScalarAsync());
                Assert.Contains("administrativeAction", definition, StringComparison.Ordinal);
            }

            await SeedAdministrativeNodeExecutionAsync(connectionString);

            await using (var dataSource = BuildDataSource(connectionString))
            {
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
                    .Options;
                await using var context = new AppDbContext(options);
                Assert.Empty(await context.AdministrativeActionBatches
                    .AsNoTracking()
                    .ToListAsync());
            }

            await connection.CloseAsync();
            await MigrateAsync(connectionString, PreviousMigration);

            await connection.OpenAsync();
            await using var verifyDown = new NpgsqlCommand("""
                SELECT
                    (SELECT count(*)
                     FROM information_schema.tables
                     WHERE table_schema = 'flowbit'
                       AND table_name LIKE 'administrative_action_batch%'),
                    (SELECT count(*)
                     FROM information_schema.columns
                     WHERE table_schema = 'flowbit'
                       AND (
                           (table_name = 'user_tasks'
                            AND column_name IN (
                                'AdministrativeActionBatchId',
                                'CompletionKind',
                                'CompletionReason'))
                           OR
                           (table_name = 'instance_history'
                            AND column_name IN ('AdministrativeActionBatchId', 'Reason'))
                           OR
                           (table_name = 'workflow_instance_version_changes'
                            AND column_name = 'AdministrativeActionBatchId')
                           )),
                    (SELECT count(*)
                     FROM flowbit.engine_settings
                     WHERE "Namespace" = 'WorkflowBatchActions')
                """, connection);
            await using var downReader = await verifyDown.ExecuteReaderAsync();
            Assert.True(await downReader.ReadAsync());
            Assert.Equal(0, downReader.GetInt64(0));
            Assert.Equal(0, downReader.GetInt64(1));
            // The historical migration seeded these two settings and did not
            // remove them in Down; the repair restores that exact legacy state.
            Assert.Equal(2, downReader.GetInt64(2));
            await downReader.CloseAsync();

            await using var downConstraint = new NpgsqlCommand("""
                SELECT pg_get_constraintdef(oid)
                FROM pg_catalog.pg_constraint
                WHERE conname = 'CK_node_executions_completion_reason'
                  AND conrelid = 'flowbit.node_executions'::regclass
                """, connection);
            var restoredDefinition = Assert.IsType<string>(
                await downConstraint.ExecuteScalarAsync());
            Assert.DoesNotContain(
                "administrativeAction",
                restoredDefinition,
                StringComparison.Ordinal);

            await using var downgradedReason = new NpgsqlCommand("""
                SELECT "CompletionReason"
                FROM flowbit.node_executions
                WHERE "CompletedBy" = 'migration-downgrade-test'
                """, connection);
            Assert.Equal("userAction", await downgradedReason.ExecuteScalarAsync());
        });
    }

    [Fact]
    public async Task RepairMigrationNoOpsForAnAlreadyCurrentSchema()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var removeHistory = new NpgsqlCommand("""
                    DELETE FROM flowbit."__EFMigrationsHistory"
                    WHERE "MigrationId" =
                        '20260808120000_RepairPositionFirstAdministrativeActionBatches'
                    """, connection);
                Assert.Equal(1, await removeHistory.ExecuteNonQueryAsync());
            }

            await MigrateAsync(connectionString, TargetMigration);

            await using var verify = new NpgsqlConnection(connectionString);
            await verify.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT count(*)
                FROM information_schema.columns
                WHERE table_schema = 'flowbit'
                  AND table_name = 'administrative_action_batches'
                  AND column_name = 'ActionKind'
                """, verify);
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        });
    }

    [Fact]
    public async Task RepairMigrationPreservesPopulatedSingleMappingLegacyAuditRows()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, LegacyBatchMigration);
            var seed = await SeedLegacyAdministrativeBatchAsync(connectionString);

            await MigrateAsync(connectionString, TargetMigration);

            await using var verify = new NpgsqlConnection(connectionString);
            await verify.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT
                    batch."Id",
                    batch."WorkflowDefinitionId",
                    batch."SourceNodeId",
                    batch."ActionKind",
                    batch."FlowId",
                    batch."TotalAffectedTaskCount",
                    batch."ActionSnapshotJson" ->> 'flowName',
                    item."PositionKind",
                    item."UserTaskId",
                    item."MultiInstanceExecutionId",
                    item."TokenActivationId",
                    item."SourceNodeId",
                    item."AffectedTaskCount",
                    item."ResultJson" -> 'legacyMappingFirst' ->> 'newUserTaskId',
                    item."ResultJson" -> 'legacyMappingFirst' ->> 'capturedInstanceUpdatedAt',
                    item."ResultJson" -> 'legacyMappingFirst' ->> 'observedCurrentTokenActivationId',
                    (SELECT count(*) FROM flowbit.user_tasks AS task
                     WHERE task."AdministrativeActionBatchId" = batch."Id"),
                    (SELECT count(*) FROM flowbit.instance_history AS history
                     WHERE history."AdministrativeActionBatchId" = batch."Id"),
                    occurrence."AdministrativeActionJson" ->> 'batchId',
                    occurrence."AdministrativeActionJson" ->> 'actionKind',
                    summary."LastActionAdministrativeActionJson" ->> 'batchId',
                    summary."LastTraversalAdministrativeActionJson" ->> 'batchId'
                FROM flowbit.administrative_action_batches AS batch
                JOIN flowbit.administrative_action_batch_items AS item
                  ON item."BatchId" = batch."Id"
                JOIN flowbit.sequence_flow_occurrences AS occurrence
                  ON occurrence."InstanceId" = item."InstanceId"
                 AND occurrence."SequenceFlowId" = item."FlowId"
                JOIN flowbit.sequence_flow_summaries AS summary
                  ON summary."InstanceId" = item."InstanceId"
                 AND summary."SequenceFlowId" = item."FlowId"
                WHERE batch."Id" = @batch_id
                """, verify);
            command.Parameters.AddWithValue("batch_id", seed.BatchId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(seed.BatchId, reader.GetInt64(0));
            Assert.Equal(seed.WorkflowDefinitionId, reader.GetInt64(1));
            Assert.Equal(3, reader.GetInt32(2));
            Assert.Equal("directFlow", reader.GetString(3));
            Assert.Equal(104, reader.GetInt32(4));
            Assert.Equal(1, reader.GetInt32(5));
            Assert.Equal("Legacy return", reader.GetString(6));
            Assert.Equal("userTask", reader.GetString(7));
            Assert.Equal(seed.SourceUserTaskId, reader.GetInt64(8));
            Assert.True(reader.IsDBNull(9));
            Assert.Equal(Guid.Empty, reader.GetGuid(10));
            Assert.Equal(3, reader.GetInt32(11));
            Assert.Equal(1, reader.GetInt32(12));
            Assert.Equal(seed.NewUserTaskId.ToString(), reader.GetString(13));
            Assert.False(string.IsNullOrWhiteSpace(reader.GetString(14)));
            Assert.Equal(seed.TokenActivationId.ToString(), reader.GetString(15));
            Assert.Equal(1L, reader.GetInt64(16));
            Assert.Equal(1L, reader.GetInt64(17));
            Assert.Equal(seed.BatchId.ToString(), reader.GetString(18));
            Assert.Equal("directFlow", reader.GetString(19));
            Assert.Equal(seed.BatchId.ToString(), reader.GetString(20));
            Assert.Equal(seed.BatchId.ToString(), reader.GetString(21));
            await reader.CloseAsync();

            await using var oldColumns = new NpgsqlCommand("""
                SELECT count(*)
                FROM information_schema.columns
                WHERE table_schema = 'flowbit'
                  AND
                  (
                      (table_name = 'administrative_action_batches'
                       AND column_name = 'FlowMappingsJson')
                      OR
                      (table_name = 'administrative_action_batch_items'
                       AND column_name IN
                       (
                           'CapturedInstanceUpdatedAt',
                           'CapturedUserTaskUpdatedAt',
                           'NewUserTaskId'
                       ))
                  )
                """, verify);
            Assert.Equal(0L, await oldColumns.ExecuteScalarAsync());
        });
    }

    [Fact]
    public async Task RepairMigrationRefusesNonterminalLegacyBatchesWithoutChangingData()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, LegacyBatchMigration);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO flowbit.administrative_action_batches
                    (
                        "WorkflowKey", "FlowMappingsJson", "Reason",
                        "CommonVariablesJson", "SelectionJson", "Status",
                        "PreparedBy", "PreparedByRolesJson",
                        "TotalItemCount", "EligibleItemCount",
                        "IneligibleItemCount", "QueuedItemCount",
                        "SucceededItemCount", "SkippedItemCount",
                        "FailedItemCount", "CancelledItemCount",
                        "CreatedAt", "UpdatedAt"
                    )
                    VALUES
                    (
                        'legacy-audit',
                        '[{"workflowDefinitionId":1,"workflowVersion":1,"flowId":2,"sourceNodeId":1,"targetNodeId":2}]'::jsonb,
                        'preserve this audit row', '{}'::jsonb, '{}'::jsonb,
                        'ready', 'legacy-operator', '[]'::jsonb,
                        0, 0, 0, 0, 0, 0, 0, 0,
                        CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                    )
                    """, connection);
                Assert.Equal(1, await seed.ExecuteNonQueryAsync());
            }

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => MigrateAsync(connectionString, TargetMigration));
            Assert.Contains(
                "Cannot upgrade mapping-first administrative batches while nonterminal batches exist",
                exception.ToString(),
                StringComparison.Ordinal);

            await using var verify = new NpgsqlConnection(connectionString);
            await verify.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT
                    EXISTS
                    (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'flowbit'
                          AND table_name = 'administrative_action_batches'
                          AND column_name = 'FlowMappingsJson'
                    ),
                    EXISTS
                    (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'flowbit'
                          AND table_name = 'administrative_action_batches'
                          AND column_name = 'ActionKind'
                    ),
                    (SELECT count(*) FROM flowbit.administrative_action_batches)
                """, verify);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
            Assert.Equal(1L, reader.GetInt64(2));
        });
    }

    [Fact]
    public async Task RepairMigrationRejectsAPartiallyUpgradedCurrentSchema()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var createDrift = new NpgsqlCommand("""
                    DELETE FROM flowbit."__EFMigrationsHistory"
                    WHERE "MigrationId" =
                        '20260808120000_RepairPositionFirstAdministrativeActionBatches';

                    ALTER TABLE flowbit.administrative_action_batch_items
                        DROP COLUMN "AffectedTaskCount" CASCADE;
                    """, connection);
                await createDrift.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => MigrateAsync(connectionString, TargetMigration));
            Assert.Contains(
                "administrative batch schema is partially upgraded",
                exception.ToString(),
                StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task<LegacyAdministrativeBatchSeed>
        SeedLegacyAdministrativeBatchAsync(string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        await using var dataSource = dataSourceBuilder.Build();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        var now = DateTimeOffset.UtcNow;
        var capturedInstanceAt = now.AddMinutes(-5);
        var capturedTaskAt = now.AddMinutes(-4);
        var activationId = Guid.NewGuid();
        var workflowKey = $"legacy-administrative-{Guid.NewGuid():N}";
        var definition = new WorkflowDefinitionEntity
        {
            Name = "Legacy administrative audit",
            WorkflowKey = workflowKey,
            Version = 1,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = "Legacy administrative audit"
            },
            IsPublished = true,
            IsDefault = false,
            CreatedAt = now
        };
        context.WorkflowDefinitions.Add(definition);
        await context.SaveChangesAsync();

        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            Status = "running",
            StartedBy = "legacy-operator",
            CreatedAt = now,
            UpdatedAt = now
        };
        context.WorkflowInstances.Add(instance);
        await context.SaveChangesAsync();

        var token = new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = 2,
            NodeName = "Returned work",
            NodeType = BpmnFlowNodeTypes.UserTask,
            ActivationId = activationId,
            Status = ExecutionTokenStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.ExecutionTokens.Add(token);
        await context.SaveChangesAsync();

        var sourceTask = new UserTaskEntity
        {
            InstanceId = instance.Id,
            TokenId = token.Id,
            NodeId = 3,
            NodeName = "Approved work",
            Status = UserTaskStatuses.Completed,
            SelectedFlowId = 104,
            CompletedBy = "legacy-operator",
            CompletionKind = "administrativeAction",
            CompletionReason = "legacy correction",
            CreatedAt = capturedTaskAt,
            UpdatedAt = capturedTaskAt,
            CompletedAt = capturedTaskAt
        };
        var newTask = new UserTaskEntity
        {
            InstanceId = instance.Id,
            TokenId = token.Id,
            NodeId = 2,
            NodeName = "Returned work",
            Status = UserTaskStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.UserTasks.AddRange(sourceTask, newTask);
        await context.SaveChangesAsync();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var mappingJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                workflowDefinitionId = definition.Id,
                workflowVersion = 1,
                flowId = 104,
                flowExternalId = "legacy-return",
                flowName = "Legacy return",
                sourceNodeId = 3,
                sourceNodeName = "Approved work",
                targetNodeId = 2,
                targetNodeName = "Returned work",
                roles = Array.Empty<string>(),
                variables = Array.Empty<object>()
            }
        });

        await using var addBatch = new NpgsqlCommand("""
            INSERT INTO flowbit.administrative_action_batches
            (
                "WorkflowKey", "FlowMappingsJson", "Reason",
                "CommonVariablesJson", "SelectionJson", "Status",
                "PreparedBy", "PreparedByRolesJson", "ConfirmedBy",
                "ConfirmedByRolesJson", "TotalItemCount",
                "EligibleItemCount", "IneligibleItemCount",
                "QueuedItemCount", "SucceededItemCount", "SkippedItemCount",
                "FailedItemCount", "CancelledItemCount", "CreatedAt",
                "UpdatedAt", "PreparedAt", "ConfirmedAt", "StartedAt",
                "CompletedAt"
            )
            VALUES
            (
                @workflow_key, @mappings, 'legacy correction', '{}'::jsonb,
                '{"mode":"explicit"}'::jsonb, 'completed',
                'legacy-operator', '["legacy-role"]'::jsonb,
                'legacy-operator', '["legacy-role"]'::jsonb,
                1, 0, 0, 0, 1, 0, 0, 0,
                @now, @now, @now, @now, @now, @now
            )
            RETURNING "Id"
            """, connection, transaction);
        addBatch.Parameters.AddWithValue("workflow_key", workflowKey);
        addBatch.Parameters.Add(
            new NpgsqlParameter("mappings", NpgsqlDbType.Jsonb)
            {
                Value = mappingJson
            });
        addBatch.Parameters.AddWithValue("now", now);
        var batchId = (long)(await addBatch.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The legacy batch was not inserted."));

        await using var addAudit = new NpgsqlCommand("""
            UPDATE flowbit.user_tasks
            SET "AdministrativeActionBatchId" = @batch_id
            WHERE "Id" = @source_task_id;

            INSERT INTO flowbit.administrative_action_batch_items
            (
                "BatchId", "InstanceId", "UserTaskId", "TokenId",
                "WorkflowDefinitionId", "FlowId",
                "CapturedInstanceUpdatedAt", "CapturedUserTaskUpdatedAt",
                "Status", "ResultJson", "NewUserTaskId", "CreatedAt",
                "UpdatedAt", "PreparedAt", "StartedAt", "CompletedAt"
            )
            VALUES
            (
                @batch_id, @instance_id, @source_task_id, @token_id,
                @definition_id, 104, @captured_instance_at, @captured_task_at,
                'succeeded', jsonb_build_object
                (
                    'workflowDefinitionId', @definition_id,
                    'selectedFlowId', 104,
                    'targetNodeId', 2,
                    'newUserTaskId', @new_task_id
                ),
                @new_task_id, @now, @now, @now, @now, @now
            );

            INSERT INTO flowbit.instance_history
            (
                "InstanceId", "WorkflowDefinitionId", "ActionId",
                "FromStepId", "ToStepId", "PerformedBy", "Note",
                "PerformedAt", "TokenId", "UserTaskId",
                "AdministrativeActionBatchId", "Reason"
            )
            VALUES
            (
                @instance_id, @definition_id, 104, 3, 2,
                'legacy-operator', 'administrativeAction', @now,
                @token_id, @source_task_id, @batch_id, 'legacy correction'
            );

            INSERT INTO flowbit.sequence_flow_occurrences
            (
                "InstanceId", "WorkflowDefinitionId", "SequenceFlowId",
                "SourceNodeId", "TargetNodeId", "TokenId", "UserTaskId",
                "Kind", "IsAction", "IsTraversal", "User", "UserRoles",
                "ValuesJson", "OccurredAt"
            )
            VALUES
            (
                @instance_id, @definition_id, 104, 3, 2,
                @token_id, @source_task_id, 'administrativeAction', TRUE, TRUE,
                'legacy-operator', ARRAY['legacy-role']::text[], '{}'::jsonb,
                @now
            );

            INSERT INTO flowbit.sequence_flow_summaries
            (
                "InstanceId", "SequenceFlowId", "ActionCount",
                "LastActionUser", "LastActionUserRoles",
                "LastActionOccurredAt", "LastActionKind",
                "LastActionValuesJson", "TraversalCount",
                "LastTraversalUser", "LastTraversalUserRoles",
                "LastTraversalOccurredAt", "LastTraversalKind",
                "LastTraversalValuesJson"
            )
            VALUES
            (
                @instance_id, 104, 1, 'legacy-operator',
                ARRAY['legacy-role']::text[], @now, 'administrativeAction',
                '{}'::jsonb, 1, 'legacy-operator',
                ARRAY['legacy-role']::text[], @now, 'administrativeAction',
                '{}'::jsonb
            );
            """, connection, transaction);
        addAudit.Parameters.AddWithValue("batch_id", batchId);
        addAudit.Parameters.AddWithValue("instance_id", instance.Id);
        addAudit.Parameters.AddWithValue("definition_id", definition.Id);
        addAudit.Parameters.AddWithValue("source_task_id", sourceTask.Id);
        addAudit.Parameters.AddWithValue("new_task_id", newTask.Id);
        addAudit.Parameters.AddWithValue("token_id", token.Id);
        addAudit.Parameters.AddWithValue("captured_instance_at", capturedInstanceAt);
        addAudit.Parameters.AddWithValue("captured_task_at", capturedTaskAt);
        addAudit.Parameters.AddWithValue("now", now);
        await addAudit.ExecuteNonQueryAsync();
        await transaction.CommitAsync();

        return new LegacyAdministrativeBatchSeed(
            batchId,
            definition.Id,
            sourceTask.Id,
            newTask.Id,
            activationId);
    }

    private static async Task SeedAdministrativeNodeExecutionAsync(
        string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        await using var dataSource = dataSourceBuilder.Build();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        var now = DateTimeOffset.UtcNow;
        var workflowKey = $"migration-downgrade-{Guid.NewGuid():N}";
        var definition = new WorkflowDefinitionEntity
        {
            Name = "Administrative migration downgrade",
            WorkflowKey = workflowKey,
            Version = 1,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = "Administrative migration downgrade"
            },
            IsPublished = true,
            IsDefault = false,
            CreatedAt = now
        };
        context.WorkflowDefinitions.Add(definition);
        await context.SaveChangesAsync();

        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            Status = "running",
            StartedBy = "migration-downgrade-test",
            CreatedAt = now,
            UpdatedAt = now
        };
        context.WorkflowInstances.Add(instance);
        await context.SaveChangesAsync();

        var token = new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = 1,
            NodeName = "Approval",
            NodeType = BpmnFlowNodeTypes.UserTask,
            Status = ExecutionTokenStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.ExecutionTokens.Add(token);
        await context.SaveChangesAsync();

        context.NodeExecutions.Add(new NodeExecutionEntity
        {
            InstanceId = instance.Id,
            WorkflowDefinitionId = definition.Id,
            ExecutionTokenId = token.Id,
            NodeId = token.NodeId,
            NodeName = token.NodeName,
            NodeType = token.NodeType,
            ExecutionKind = NodeExecutionKinds.Node,
            Status = NodeExecutionStatuses.Completed,
            CompletionReason = "administrativeAction",
            CompletedBy = "migration-downgrade-test",
            CreatedAt = now,
            StartedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        });
        await context.SaveChangesAsync();
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "administrative_batches_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "postgres"
        };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{databaseName}\"",
                admin);
            await create.ExecuteNonQueryAsync();
        }

        var databaseBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = databaseName
        };
        try
        {
            await test(databaseBuilder.ConnectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE \"{databaseName}\" WITH (FORCE)",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task MigrateAsync(
        string connectionString,
        string targetMigration)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        await using var dataSource = dataSourceBuilder.Build();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static NpgsqlDataSource BuildDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.EnableDynamicJson();
        return builder.Build();
    }

    private sealed record LegacyAdministrativeBatchSeed(
        long BatchId,
        long WorkflowDefinitionId,
        long SourceUserTaskId,
        long NewUserTaskId,
        Guid TokenActivationId);
}
