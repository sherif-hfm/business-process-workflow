using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class ErrorEndMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20260719162306_AddUserTaskCompletedByRoles";

    [Fact]
    public async Task MigrationBackfillsLegacyFaultDescriptionAndLeavesCodeNull()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var tokenId = await SeedLegacyFaultedTokenAsync(connectionString);

            await MigrateAsync(connectionString);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT "FaultCode", "FaultDescription"
                FROM flowbit.execution_tokens
                WHERE "Id" = @tokenId
                """, connection);
            command.Parameters.AddWithValue("tokenId", tokenId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.Equal("Legacy failure", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
        });
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "error_end_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "postgres"
        };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task MigrateAsync(string connectionString, string? targetMigration = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private static async Task<long> SeedLegacyFaultedTokenAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var definition = new NpgsqlCommand("""
            INSERT INTO workflow_definitions
                ("Name", "WorkflowKey", "Version", "Definition", "IsPublished", "IsDefault", "CreatedAt")
            VALUES
                ('Legacy error end migration', 'legacy-error-end', 1, @definition, true, true, now())
            RETURNING "Id"
            """, connection, transaction);
        definition.Parameters.AddWithValue(
            "definition",
            NpgsqlDbType.Jsonb,
            """
            {"id":"legacy-error-end","name":"Legacy error end migration","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}
            """);
        var definitionId = (long)(await definition.ExecuteScalarAsync())!;

        await using var instance = new NpgsqlCommand("""
            INSERT INTO workflow_instances
                ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy", "CreatedAt", "UpdatedAt")
            VALUES
                (@definitionId, 'legacy-error-end', 'faulted', 'legacy-user', now(), now())
            RETURNING "Id"
            """, connection, transaction);
        instance.Parameters.AddWithValue("definitionId", definitionId);
        var instanceId = (long)(await instance.ExecuteScalarAsync())!;

        await using var token = new NpgsqlCommand("""
            INSERT INTO execution_tokens
                ("InstanceId", "NodeId", "NodeName", "NodeType", "Status", "CreatedAt", "UpdatedAt")
            VALUES
                (@instanceId, 9, 'Legacy failure', 'errorEndEvent', 'faulted', now(), now())
            RETURNING "Id"
            """, connection, transaction);
        token.Parameters.AddWithValue("instanceId", instanceId);
        var tokenId = (long)(await token.ExecuteScalarAsync())!;

        await transaction.CommitAsync();
        return tokenId;
    }
}
