using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class DatabaseSchemaTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task AllFlowbitTablesAndMigrationHistoryUseFlowbitSchema()
    {
        await using var db = fixture.CreateDbContext();
        var mappedTables = db.Model.GetEntityTypes()
            .Select(entity => new
            {
                Name = entity.GetTableName(),
                Schema = entity.GetSchema()
            })
            .Where(table => table.Name is not null)
            .Distinct()
            .ToArray();

        Assert.Equal(37, mappedTables.Length);
        Assert.All(mappedTables, table => Assert.Equal(FlowbitDatabase.Schema, table.Schema));
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_current_values");
        Assert.Contains(mappedTables, table => table.Name == "gateway_executions");
        Assert.Contains(mappedTables, table => table.Name == "gateway_branches");
        Assert.Contains(mappedTables, table => table.Name == "complex_gateway_states");
        Assert.DoesNotContain(mappedTables, table => table.Name == "parallel_gateway_executions");
        Assert.DoesNotContain(mappedTables, table => table.Name == "parallel_gateway_branches");
        Assert.Contains(mappedTables, table => table.Name == "node_executions");
        Assert.Contains(mappedTables, table => table.Name == "user_delegations");
        Assert.Contains(mappedTables, table => table.Name == "workflow_delegation_policies");
        Assert.Contains(mappedTables, table => table.Name == "workflow_jobs");
        Assert.Contains(mappedTables, table => table.Name == "workflow_job_attempts");
        Assert.Contains(mappedTables, table => table.Name == "workflow_job_snapshots");
        Assert.Contains(mappedTables, table => table.Name == "workflow_incidents");
        Assert.Contains(mappedTables, table => table.Name == "timer_subscriptions");
        Assert.Contains(mappedTables, table => table.Name == "workflow_instance_version_changes");
        Assert.Contains(mappedTables, table => table.Name == "workflow_instance_version_change_batches");
        Assert.Contains(mappedTables, table => table.Name == "workflow_instance_version_change_batch_items");
        Assert.Contains(mappedTables, table => table.Name == "administrative_action_batches");
        Assert.Contains(mappedTables, table => table.Name == "administrative_action_batch_items");
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_updates");
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_update_batches");
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_update_batch_items");
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_update_batch_jobs");

        var expectedNames = mappedTables
            .Select(table => table.Name!)
            .Append(FlowbitDatabase.MigrationsHistoryTable)
            .Order(StringComparer.Ordinal)
            .ToArray();

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT schemaname, tablename
            FROM pg_catalog.pg_tables
            WHERE tablename = ANY (@tableNames)
            ORDER BY schemaname, tablename
            """, connection);
        command.Parameters.AddWithValue("tableNames", expectedNames);

        var actual = new List<(string Schema, string Table)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal(expectedNames.Length, actual.Count);
        Assert.All(actual, table => Assert.Equal(FlowbitDatabase.Schema, table.Schema));
        Assert.Equal(expectedNames, actual.Select(table => table.Table).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkflowJobOperationsIndexesAreApplied()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT indexname
            FROM pg_catalog.pg_indexes
            WHERE schemaname = 'flowbit'
              AND tablename = 'workflow_jobs'
              AND indexname = ANY (@indexNames)
            ORDER BY indexname
            """,
            connection);
        var expected = new[]
        {
            "IX_workflow_jobs_status_updated_id",
            "IX_workflow_jobs_updated_id"
        };
        command.Parameters.AddWithValue("indexNames", expected);

        var actual = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add(reader.GetString(0));
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task InstanceVersionChangeBatchClassificationCountsAreApplied()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_constraintdef(constraint_row.oid)
            FROM pg_catalog.pg_constraint AS constraint_row
            WHERE constraint_row.connamespace = 'flowbit'::regnamespace
              AND constraint_row.conrelid =
                  'flowbit.workflow_instance_version_change_batches'::regclass
              AND constraint_row.conname =
                  'CK_workflow_instance_version_change_batches_counts'
              AND EXISTS (
                  SELECT 1
                  FROM information_schema.columns AS column_row
                  WHERE column_row.table_schema = 'flowbit'
                    AND column_row.table_name =
                        'workflow_instance_version_change_batches'
                    AND column_row.column_name = 'BlockedItemCount')
            """,
            connection);
        var definition = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Contains(
            "\"BlockedItemCount\" <= \"IneligibleItemCount\"",
            definition,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"StaleItemCount\" <= (\"IneligibleItemCount\" + \"SkippedItemCount\")",
            definition,
            StringComparison.Ordinal);
    }
}
