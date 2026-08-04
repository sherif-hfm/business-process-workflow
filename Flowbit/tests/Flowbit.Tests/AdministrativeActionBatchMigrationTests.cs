using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionBatchMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration =
        "20260803175244_AddSettingDescriptionsAndManagementRole";
    private const string TargetMigration =
        "20260804155154_AddAdministrativeActionBatches";

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
                           'TargetWorkflowDefinitionId',
                           'FlowExternalId'))
                      OR
                      (table_name = 'administrative_action_batch_items'
                       AND column_name IN (
                           'WorkflowDefinitionId',
                           'FlowId',
                           'SourceWorkflowDefinitionId',
                           'TargetWorkflowDefinitionId',
                           'VersionChangeAuditId')))
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
                        "administrative_action_batch_items.FlowId",
                        "administrative_action_batch_items.WorkflowDefinitionId",
                        "administrative_action_batches.FlowMappingsJson"
                    ],
                    columns);
            }

            await using (var command = new NpgsqlCommand("""
                SELECT "Namespace" || '.' || "Key", "Value", "Description"
                FROM flowbit.engine_settings
                WHERE ("Namespace", "Key") IN (
                    ('WorkflowBatchActions', 'RequiredRole'),
                    ('WorkflowBatchActions', 'MaxItems'))
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
                Assert.Equal(2, settings.Count);
                Assert.Contains(settings, row =>
                    row.Key == "WorkflowBatchActions.RequiredRole"
                    && row.Value == "admin");
                Assert.Contains(settings, row =>
                    row.Key == "WorkflowBatchActions.MaxItems"
                    && row.Value == "10000");
                Assert.All(settings, row => Assert.False(string.IsNullOrWhiteSpace(row.Description)));
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
}
