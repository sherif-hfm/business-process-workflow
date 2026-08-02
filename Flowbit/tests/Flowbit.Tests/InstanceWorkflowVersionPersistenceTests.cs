using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceWorkflowVersionPersistenceTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20260802150520_SeedDefaultSettings";
    private const string TargetMigration = "20260802163119_AddInstanceWorkflowVersionChanges";

    [Fact]
    public async Task MigrationBackfillsRequiredDefinitionProvenanceFromOwningInstance()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var definitionId = await SeedLegacyRuntimeRowsAsync(connectionString);

            await MigrateAsync(connectionString, TargetMigration);

            var rows = await ReadProvenanceRowsAsync(connectionString);
            Assert.Equal(
                new[]
                {
                    "instance_history",
                    "node_executions",
                    "sequence_flow_occurrences"
                },
                rows.Select(row => row.TableName));
            Assert.All(rows, row =>
            {
                Assert.Equal(definitionId, row.WorkflowDefinitionId);
                Assert.Equal("NO", row.IsNullable);
            });
        });
    }

    [Fact]
    public async Task DeleteReturnsDomainConflictForAuditAndHistoricalRuntimeReferences()
    {
        var key = $"version-delete-{Guid.NewGuid():N}";
        long auditSourceId;
        long historySourceId;

        await using (var setup = fixture.CreateDbContext())
        {
            var auditSource = NewDefinition(key, 1, "Audit source");
            var historySource = NewDefinition(key, 2, "History source");
            var target = NewDefinition(key, 3, "Target");
            setup.WorkflowDefinitions.AddRange(auditSource, historySource, target);
            await setup.SaveChangesAsync();

            var instance = new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = target.Id,
                WorkflowKey = key,
                Status = "running",
                StartedBy = "migration-test",
                Tokens =
                [
                    new ExecutionTokenEntity
                    {
                        NodeId = 1,
                        NodeName = "Deletion-reference wait state",
                        NodeType = BpmnFlowNodeTypes.UserTask,
                        Status = ExecutionTokenStatuses.Active
                    }
                ]
            };
            setup.WorkflowInstances.Add(instance);
            await setup.SaveChangesAsync();

            setup.WorkflowInstanceVersionChanges.Add(new WorkflowInstanceVersionChangeEntity
            {
                InstanceId = instance.Id,
                SourceWorkflowDefinitionId = auditSource.Id,
                TargetWorkflowDefinitionId = target.Id,
                ChangedBy = "admin",
                ChangedByRolesJson = JsonDocument.Parse("[\"admin\"]"),
                Reason = "Preserve the immutable audit reference."
            });
            setup.InstanceHistory.Add(new InstanceHistoryEntity
            {
                InstanceId = instance.Id,
                WorkflowDefinitionId = historySource.Id,
                FromStepId = 1,
                ToStepId = 2,
                Note = "Historical source-version visit"
            });
            await setup.SaveChangesAsync();
            auditSourceId = auditSource.Id;
            historySourceId = historySource.Id;
        }

        await AssertDeleteConflictAsync(auditSourceId);
        await AssertDeleteConflictAsync(historySourceId);
    }

    private async Task AssertDeleteConflictAsync(long definitionId)
    {
        await using var context = fixture.CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = new WorkflowDefinitionRepository(context, cache);

        var exception = await Assert.ThrowsAsync<WorkflowConflictException>(
            () => repository.DeleteAsync(definitionId, CancellationToken.None));

        Assert.Contains("runtime or version-change history", exception.Message);
        Assert.True(await context.WorkflowDefinitions.AnyAsync(
            definition => definition.Id == definitionId));
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "instance_version_backfill_" + Guid.NewGuid().ToString("N");
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
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task<long> SeedLegacyRuntimeRowsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH definition_row AS (
                INSERT INTO flowbit.workflow_definitions
                    ("Name", "WorkflowKey", "Version", "Definition",
                     "IsPublished", "IsDefault", "CreatedAt")
                VALUES
                    ('Version provenance backfill', 'version-provenance-backfill', 1,
                     '{"id":"version-provenance-backfill","name":"Version provenance backfill","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}'::jsonb,
                     true, false, TIMESTAMPTZ '2026-01-01 00:00:00+00')
                RETURNING "Id"
            ),
            instance_row AS (
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy",
                     "CreatedAt", "UpdatedAt")
                SELECT "Id", 'version-provenance-backfill', 'running', 'legacy-user',
                       TIMESTAMPTZ '2026-01-01 00:00:00+00',
                       TIMESTAMPTZ '2026-01-02 00:00:00+00'
                FROM definition_row
                RETURNING "Id", "WorkflowDefinitionId"
            ),
            token_row AS (
                INSERT INTO flowbit.execution_tokens
                    ("InstanceId", "NodeId", "NodeName", "NodeExternalId", "NodeType",
                     "Status", "CreatedAt", "UpdatedAt")
                SELECT "Id", 2, 'Approval', 'approval', 'userTask', 'active',
                       TIMESTAMPTZ '2026-01-01 00:00:00+00',
                       TIMESTAMPTZ '2026-01-02 00:00:00+00'
                FROM instance_row
                RETURNING "Id", "InstanceId"
            ),
            execution_row AS (
                INSERT INTO flowbit.node_executions
                    ("InstanceId", "ExecutionTokenId", "NodeId", "NodeName",
                     "NodeExternalId", "NodeType", "ExecutionKind", "Status",
                     "CreatedAt", "StartedAt", "UpdatedAt")
                SELECT "InstanceId", "Id", 2, 'Approval', 'approval', 'userTask',
                       'node', 'active',
                       TIMESTAMPTZ '2026-01-01 00:00:00+00',
                       TIMESTAMPTZ '2026-01-01 00:00:00+00',
                       TIMESTAMPTZ '2026-01-02 00:00:00+00'
                FROM token_row
            ),
            history_row AS (
                INSERT INTO flowbit.instance_history
                    ("InstanceId", "FromStepId", "ToStepId", "PerformedBy",
                     "Note", "PerformedAt")
                SELECT "Id", 1, 2, 'legacy-user', 'legacy transition',
                       TIMESTAMPTZ '2026-01-01 12:00:00+00'
                FROM instance_row
            ),
            occurrence_row AS (
                INSERT INTO flowbit.sequence_flow_occurrences
                    ("InstanceId", "SequenceFlowId", "SourceNodeId", "TargetNodeId",
                     "Kind", "IsAction", "IsTraversal", "OccurredAt")
                SELECT "Id", 101, 1, 2, 'automatic', false, true,
                       TIMESTAMPTZ '2026-01-01 12:00:00+00'
                FROM instance_row
            )
            SELECT "WorkflowDefinitionId"
            FROM instance_row
            """,
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<ProvenanceRow>> ReadProvenanceRowsAsync(
        string connectionString)
    {
        var rows = new List<ProvenanceRow>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT runtime.table_name, runtime.definition_id, columns.is_nullable
            FROM (
                SELECT 'instance_history'::text AS table_name,
                       "WorkflowDefinitionId" AS definition_id
                FROM flowbit.instance_history
                UNION ALL
                SELECT 'node_executions', "WorkflowDefinitionId"
                FROM flowbit.node_executions
                UNION ALL
                SELECT 'sequence_flow_occurrences', "WorkflowDefinitionId"
                FROM flowbit.sequence_flow_occurrences
            ) AS runtime
            JOIN information_schema.columns AS columns
              ON columns.table_schema = 'flowbit'
             AND columns.table_name = runtime.table_name
             AND columns.column_name = 'WorkflowDefinitionId'
            ORDER BY runtime.table_name
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ProvenanceRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2)));
        }
        return rows;
    }

    private static WorkflowDefinitionEntity NewDefinition(
        string workflowKey,
        int version,
        string name) => new()
    {
        Name = name,
        WorkflowKey = workflowKey,
        Version = version,
        IsPublished = true,
        Definition = new WorkflowModel { Id = workflowKey, Name = name }
    };

    private sealed record ProvenanceRow(
        string TableName,
        long WorkflowDefinitionId,
        string IsNullable);
}
