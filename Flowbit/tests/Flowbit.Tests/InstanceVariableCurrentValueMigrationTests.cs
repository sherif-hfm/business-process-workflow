using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVariableCurrentValueMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20260731143528_AddWorkflowJobOperationsIndexes";
    private const string TargetMigration = "20260731173620_AddInstanceVariableCurrentValueProjection";

    [Fact]
    public async Task MigrationBackfillsAndMaintainsLatestValues()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var instanceId = await SeedLegacyVariablesAsync(connectionString);

            await MigrateAsync(connectionString, TargetMigration);

            var rows = await ReadProjectionAsync(connectionString, instanceId);
            Assert.Collection(
                rows,
                center =>
                {
                    Assert.Equal("center", center.VariableName);
                    Assert.Equal(200, center.SourceVariableId);
                    Assert.Equal("\"MC-2\"", center.ValueJson);
                    Assert.Equal(
                        new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        center.SetAt);
                },
                request =>
                {
                    Assert.Equal("request", request.VariableName);
                    Assert.Equal(110, request.SourceVariableId);
                    Assert.Equal("{\"medicalCenter\": {\"id\": \"MC-2\"}}", request.ValueJson);
                });

            await InsertVariableAsync(
                connectionString,
                150,
                instanceId,
                "center",
                "\"STALE\"",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(200, (await ReadProjectionAsync(connectionString, instanceId))[0].SourceVariableId);

            await InsertVariableAsync(
                connectionString,
                300,
                instanceId,
                "center",
                "\"MC-3\"",
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var updated = (await ReadProjectionAsync(connectionString, instanceId))[0];
            Assert.Equal(300, updated.SourceVariableId);
            Assert.Equal("\"MC-3\"", updated.ValueJson);

            var indexes = await ReadProjectionIndexesAsync(connectionString);
            Assert.Contains("IX_instance_variable_current_values_VariableName_InstanceId", indexes);
            Assert.Contains("IX_instance_variable_current_values_ValueJson_gin", indexes);
            Assert.Contains("IX_iv_current_name_root_string_ci_instance", indexes);
            Assert.Contains("IX_iv_current_name_root_number_instance", indexes);

            var triggerDefinition = await ReadTriggerDefinitionAsync(connectionString);
            Assert.Contains("ON flowbit.instance_variables", triggerDefinition);
            Assert.Contains(
                "EXECUTE FUNCTION flowbit.sync_instance_variable_current_value()",
                triggerDefinition);

            await DeleteInstanceAsync(connectionString, instanceId);
            Assert.Empty(await ReadProjectionAsync(connectionString, instanceId));
        });
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "instance_variable_projection_" + Guid.NewGuid().ToString("N");
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

    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private static async Task<long> SeedLegacyVariablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH definition_row AS
            (
                INSERT INTO flowbit.workflow_definitions
                    ("Name", "WorkflowKey", "Version", "Definition",
                     "IsPublished", "IsDefault", "CreatedAt")
                VALUES
                    ('Variable projection migration', 'variable-projection', 1,
                     @definition, false, false, now())
                RETURNING "Id"
            ),
            instance_row AS
            (
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 'variable-projection', 'running', 'migration-test', now(), now()
                FROM definition_row
                RETURNING "Id"
            ),
            variable_rows AS
            (
                INSERT INTO flowbit.instance_variables
                    ("Id", "InstanceId", "VariableName", "ValueJson", "SetAt")
                SELECT 100, "Id", 'center', '"MC-1"'::jsonb,
                       TIMESTAMPTZ '2025-01-03 00:00:00+00'
                FROM instance_row
                UNION ALL
                SELECT 200, "Id", 'center', '"MC-2"'::jsonb,
                       TIMESTAMPTZ '2025-01-01 00:00:00+00'
                FROM instance_row
                UNION ALL
                SELECT 110, "Id", 'request',
                       '{"medicalCenter":{"id":"MC-2"}}'::jsonb,
                       TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM instance_row
                RETURNING "Id"
            )
            SELECT "Id"
            FROM instance_row
            """,
            connection);
        command.Parameters.AddWithValue(
            "definition",
            NpgsqlDbType.Jsonb,
            """
            {"id":"variable-projection","name":"Variable projection migration","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}
            """);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertVariableAsync(
        string connectionString,
        long id,
        long instanceId,
        string variableName,
        string valueJson,
        DateTime setAt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO flowbit.instance_variables
                ("Id", "InstanceId", "VariableName", "ValueJson", "SetAt")
            VALUES (@id, @instanceId, @variableName, @valueJson, @setAt)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("instanceId", instanceId);
        command.Parameters.AddWithValue("variableName", variableName);
        command.Parameters.AddWithValue("valueJson", NpgsqlDbType.Jsonb, valueJson);
        command.Parameters.AddWithValue("setAt", setAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<ProjectionRow>> ReadProjectionAsync(
        string connectionString,
        long instanceId)
    {
        var rows = new List<ProjectionRow>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT "VariableName", "SourceVariableId", "ValueJson"::text, "SetAt"
            FROM flowbit.instance_variable_current_values
            WHERE "InstanceId" = @instanceId
            ORDER BY "VariableName"
            """,
            connection);
        command.Parameters.AddWithValue("instanceId", instanceId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ProjectionRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetDateTime(3)));
        }

        return rows;
    }

    private static async Task<HashSet<string>> ReadProjectionIndexesAsync(string connectionString)
    {
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = 'flowbit'
              AND tablename = 'instance_variable_current_values'
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }

    private static async Task<string> ReadTriggerDefinitionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_triggerdef(trigger.oid)
            FROM pg_trigger AS trigger
            JOIN pg_class AS source_table ON source_table.oid = trigger.tgrelid
            JOIN pg_namespace AS source_schema ON source_schema.oid = source_table.relnamespace
            WHERE source_schema.nspname = 'flowbit'
              AND source_table.relname = 'instance_variables'
              AND trigger.tgname = 'instance_variables_sync_current_value'
              AND NOT trigger.tgisinternal
            """,
            connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteInstanceAsync(string connectionString, long instanceId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """DELETE FROM flowbit.workflow_instances WHERE "Id" = @instanceId""",
            connection);
        command.Parameters.AddWithValue("instanceId", instanceId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ProjectionRow(
        string VariableName,
        long SourceVariableId,
        string ValueJson,
        DateTime SetAt);
}
