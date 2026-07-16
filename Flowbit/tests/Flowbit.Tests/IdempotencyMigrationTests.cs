using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Flowbit.Infrastructure.Data;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class IdempotencyMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20260715152358_AddWorkflowBusinessKeyLinks";

    [Fact]
    public async Task MigrationBackfillsLegacyMessageStartOwner()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var instanceId = await SeedLegacyMessageStartAsync(
                connectionString,
                "legacy-family",
                "requestId",
                "  REQUEST-1  ");

            await MigrateAsync(connectionString);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT claim."InstanceId", claim."IdempotencyKey", instance."IdempotencyKey"
                FROM workflow_idempotency_claims AS claim
                JOIN workflow_instances AS instance ON instance."Id" = claim."InstanceId"
                WHERE claim."WorkflowKey" = 'legacy-family'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(instanceId, reader.GetInt64(0));
            Assert.Equal("REQUEST-1", reader.GetString(1));
            Assert.Equal("REQUEST-1", reader.GetString(2));
            Assert.False(await reader.ReadAsync());
        });
    }

    [Fact]
    public async Task MigrationRejectsCollidingLegacyKeysWithoutExposingThem()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await SeedLegacyMessageStartAsync(connectionString, "collision-family", "requestId", " SECRET-COLLISION ");
            await SeedLegacyMessageStartAsync(connectionString, "collision-family", "requestId", "SECRET-COLLISION");

            var error = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(connectionString));
            Assert.Contains("collide within a workflow family", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SECRET-COLLISION", error.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MigrationRejectsMalformedLegacyValueWithoutExposingIt()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await SeedLegacyMessageStartAsync(
                connectionString,
                "malformed-family",
                "requestId",
                "938475",
                valueIsJsonLiteral: true);

            var error = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(connectionString));
            Assert.Contains("missing, malformed, blank, or oversized", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("938475", error.ToString(), StringComparison.Ordinal);
        });
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "idempotency_" + Guid.NewGuid().ToString("N");
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
            .UseNpgsql(connectionString)
            .Options;
        await using var context = new AppDbContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private static async Task<long> SeedLegacyMessageStartAsync(
        string connectionString,
        string workflowKey,
        string variableName,
        string value,
        bool valueIsJsonLiteral = false)
    {
        var definitionJson = $$"""
            {
              "id": "{{workflowKey}}",
              "name": "Legacy idempotency migration",
              "flowNodes": [
                {
                  "id": 1,
                  "name": "Message start",
                  "type": "messageStartEvent",
                  "message": { "idempotencyVariable": "{{variableName}}", "outputMappings": [] }
                }
              ],
              "sequenceFlows": [],
              "variables": [],
              "lanes": []
            }
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var definition = new NpgsqlCommand("""
            INSERT INTO workflow_definitions
                ("Name", "WorkflowKey", "Version", "Definition", "IsPublished", "IsDefault", "CreatedAt")
            VALUES
                ('Legacy idempotency migration', @workflowKey, 1, @definition, true, true, now())
            ON CONFLICT ("Name", "Version") DO UPDATE
                SET "Name" = EXCLUDED."Name"
            RETURNING "Id"
            """, connection, transaction);
        definition.Parameters.AddWithValue("workflowKey", workflowKey);
        definition.Parameters.AddWithValue("definition", NpgsqlDbType.Jsonb, definitionJson);
        var definitionId = (long)(await definition.ExecuteScalarAsync())!;

        await using var instance = new NpgsqlCommand("""
            INSERT INTO workflow_instances
                ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy", "CreatedAt", "UpdatedAt")
            VALUES
                (@definitionId, @workflowKey, 'running', 'legacy-client', now(), now())
            RETURNING "Id"
            """, connection, transaction);
        instance.Parameters.AddWithValue("definitionId", definitionId);
        instance.Parameters.AddWithValue("workflowKey", workflowKey);
        var instanceId = (long)(await instance.ExecuteScalarAsync())!;

        await using var history = new NpgsqlCommand("""
            INSERT INTO instance_history
                ("InstanceId", "FromStepId", "ToStepId", "PerformedBy", "Note", "PerformedAt")
            VALUES
                (@instanceId, 1, 2, 'legacy-client', 'messageStart', now())
            """, connection, transaction);
        history.Parameters.AddWithValue("instanceId", instanceId);
        await history.ExecuteNonQueryAsync();

        await using var variable = new NpgsqlCommand("""
            INSERT INTO instance_variables
                ("InstanceId", "VariableName", "ValueJson", "SetBy", "SetAt")
            VALUES
                (@instanceId, @variableName, @value, 'legacy-client', now())
            """, connection, transaction);
        variable.Parameters.AddWithValue("instanceId", instanceId);
        variable.Parameters.AddWithValue("variableName", variableName);
        variable.Parameters.AddWithValue(
            "value",
            NpgsqlDbType.Jsonb,
            valueIsJsonLiteral ? value : System.Text.Json.JsonSerializer.Serialize(value));
        await variable.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
        return instanceId;
    }
}
