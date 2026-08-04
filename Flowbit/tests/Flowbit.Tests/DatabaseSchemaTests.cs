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

        Assert.Equal(31, mappedTables.Length);
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
        Assert.Contains(mappedTables, table => table.Name == "administrative_action_batches");
        Assert.Contains(mappedTables, table => table.Name == "administrative_action_batch_items");

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
}
