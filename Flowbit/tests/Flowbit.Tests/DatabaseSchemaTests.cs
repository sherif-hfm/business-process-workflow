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

        Assert.Equal(18, mappedTables.Length);
        Assert.All(mappedTables, table => Assert.Equal(FlowbitDatabase.Schema, table.Schema));
        Assert.Contains(mappedTables, table => table.Name == "parallel_gateway_executions");
        Assert.Contains(mappedTables, table => table.Name == "parallel_gateway_branches");

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
}
