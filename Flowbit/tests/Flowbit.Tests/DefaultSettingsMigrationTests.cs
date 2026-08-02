using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Flowbit.Infrastructure.Data;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class DefaultSettingsMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration =
        "20260801100321_AddAutomaticActivationLoopGuard";
    private const string TargetMigration =
        "20260802150520_SeedDefaultSettings";

    [Fact]
    public async Task FreshDatabaseSeedsCanonicalDefaults()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);

            var settings = await ReadSettingsAsync(connectionString);

            Assert.Equal(
                new[]
                {
                    "Delegation/AdminRoles/admin",
                    "NodeExecution/RequiredRole/admin",
                    "Workflow/RequiredRole/admin",
                    "Workflow.Async/MaxConsecutiveAutomaticActivations/1000",
                    "Workflow.Gateway/MaxActiveTokens/1000",
                    "Workflow.MultiInstance/MaxInstances/1000",
                    "WorkflowInstances/RequiredRole/admin",
                    "WorkflowJobs/RequiredRole/admin"
                }.Order(StringComparer.Ordinal),
                settings.Engine
                    .Select(row => $"{row.Namespace}/{row.Key}/{row.Value}")
                    .Order(StringComparer.Ordinal));
            Assert.Equal(
                new[]
                {
                    "examples/messageClientId/example-message-client/string",
                    "examples/messageCorrelation/orders:inbound/string"
                }.Order(StringComparer.Ordinal),
                settings.Workflow
                    .Select(row =>
                        $"{row.Namespace}/{row.Name}/{row.Value}/{row.JsonType}")
                    .Order(StringComparer.Ordinal));
            Assert.DoesNotContain(
                settings.Engine,
                row => row.Namespace == "Authentication"
                       && row.Key == "UserIdentityClaim");
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UpgradePreservesCanonicalAndLegacyEquivalentOverrides(
        string? legacyNamespace)
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await SeedExistingOverridesAsync(connectionString, legacyNamespace);
            var before = await ReadSettingsAsync(connectionString);

            await MigrateAsync(connectionString, TargetMigration);

            var after = await ReadSettingsAsync(connectionString);
            Assert.Equal(8, after.Engine.Length);
            Assert.Equal(2, after.Workflow.Length);
            Assert.All(before.Engine, row => Assert.Contains(row, after.Engine));
            Assert.All(before.Workflow, row => Assert.Contains(row, after.Workflow));

            var engineByLogicalKey = after.Engine.ToDictionary(
                LogicalEngineKey,
                StringComparer.Ordinal);
            Assert.Equal(8, engineByLogicalKey.Count);
            Assert.Equal(
                "existing-workflow-admin",
                engineByLogicalKey["Workflow.RequiredRole"].Value);
            Assert.Equal(
                "legacy-instance-admin",
                engineByLogicalKey["WorkflowInstances.RequiredRole"].Value);
            Assert.Equal("admin", engineByLogicalKey["NodeExecution.RequiredRole"].Value);
            Assert.Equal(
                "1000",
                engineByLogicalKey["Workflow.MultiInstance.MaxInstances"].Value);

            var workflowByLogicalKey = after.Workflow.ToDictionary(
                LogicalWorkflowKey,
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(2, workflowByLogicalKey.Count);
            Assert.Equal(
                "existing-client",
                workflowByLogicalKey["examples.messageClientId"].Value);
            Assert.Equal(
                "existing-correlation",
                workflowByLogicalKey["examples.messageCorrelation"].Value);
            Assert.All(after.Workflow, row => Assert.Equal("string", row.JsonType));

            Assert.DoesNotContain(
                after.Engine,
                row => row.Namespace == "WorkflowInstances"
                       && row.Key == "RequiredRole");
            Assert.DoesNotContain(
                after.Workflow,
                row => row.Namespace == "examples"
                       && row.Name == "messageCorrelation");
            Assert.DoesNotContain(
                after.Engine,
                row => row.Namespace == "Authentication"
                       && row.Key == "UserIdentityClaim");
        });
    }

    [Fact]
    public async Task DownAndReapplyPreserveRowsAndOperatorChanges()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);
            await UpdateOperatorValuesAsync(connectionString);
            var expected = await ReadSettingsAsync(connectionString);

            await MigrateAsync(connectionString, PreviousMigration);
            var afterDown = await ReadSettingsAsync(connectionString);
            Assert.Equal(expected.Engine, afterDown.Engine);
            Assert.Equal(expected.Workflow, afterDown.Workflow);

            await MigrateAsync(connectionString, TargetMigration);
            var afterReapply = await ReadSettingsAsync(connectionString);
            Assert.Equal(expected.Engine, afterReapply.Engine);
            Assert.Equal(expected.Workflow, afterReapply.Workflow);
            Assert.Equal(8, afterReapply.Engine.Length);
            Assert.Equal(2, afterReapply.Workflow.Length);
        });
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "default_settings_" + Guid.NewGuid().ToString("N");
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
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private static async Task SeedExistingOverridesAsync(
        string connectionString,
        string? legacyNamespace)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO flowbit.engine_settings
                ("Namespace", "Key", "Value", "CreatedAt", "UpdatedAt")
            VALUES
                ('Workflow', 'RequiredRole', 'existing-workflow-admin',
                 '2026-01-02T03:04:05Z'::timestamptz,
                 '2026-01-03T04:05:06Z'::timestamptz),
                (@legacy_namespace, 'WorkflowInstances.RequiredRole',
                 'legacy-instance-admin',
                 '2026-01-04T05:06:07Z'::timestamptz,
                 '2026-01-05T06:07:08Z'::timestamptz);

            INSERT INTO flowbit.workflow_settings
                ("Namespace", "Name", "Value", "CreatedAt", "UpdatedAt")
            VALUES
                ('examples', 'messageClientId',
                 to_jsonb('existing-client'::text),
                 '2026-01-06T07:08:09Z'::timestamptz,
                 '2026-01-07T08:09:10Z'::timestamptz),
                (@legacy_namespace, 'examples.messageCorrelation',
                 to_jsonb('existing-correlation'::text),
                 '2026-01-08T09:10:11Z'::timestamptz,
                 '2026-01-09T10:11:12Z'::timestamptz);
            """, connection);
        var parameter = command.Parameters.Add(
            "legacy_namespace",
            NpgsqlDbType.Varchar);
        parameter.Value = legacyNamespace is null
            ? DBNull.Value
            : legacyNamespace;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateOperatorValuesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE flowbit.engine_settings
            SET "Value" = 'platform-admin',
                "UpdatedAt" = '2026-02-03T04:05:06Z'::timestamptz
            WHERE "Namespace" = 'Workflow'
              AND "Key" = 'RequiredRole';

            UPDATE flowbit.workflow_settings
            SET "Value" = to_jsonb('rotated-client'::text),
                "UpdatedAt" = '2026-02-04T05:06:07Z'::timestamptz
            WHERE "Namespace" = 'examples'
              AND "Name" = 'messageClientId';
            """, connection);
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
    }

    private static async Task<SettingsSnapshot> ReadSettingsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var engine = new List<EngineRow>();
        await using (var command = new NpgsqlCommand("""
            SELECT "Id", "Namespace", "Key", "Value",
                   "CreatedAt"::text, "UpdatedAt"::text
            FROM flowbit.engine_settings
            ORDER BY COALESCE("Namespace", ''), "Key", "Id"
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                engine.Add(new EngineRow(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            }
        }

        var workflow = new List<WorkflowRow>();
        await using (var command = new NpgsqlCommand("""
            SELECT "Id", "Namespace", "Name", "Value" #>> '{}',
                   jsonb_typeof("Value"),
                   "CreatedAt"::text, "UpdatedAt"::text
            FROM flowbit.workflow_settings
            ORDER BY COALESCE("Namespace", ''), "Name", "Id"
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                workflow.Add(new WorkflowRow(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
        }

        return new SettingsSnapshot(engine.ToArray(), workflow.ToArray());
    }

    private static string LogicalEngineKey(EngineRow row) =>
        string.IsNullOrEmpty(row.Namespace)
            ? row.Key
            : $"{row.Namespace}.{row.Key}";

    private static string LogicalWorkflowKey(WorkflowRow row) =>
        string.IsNullOrEmpty(row.Namespace)
            ? row.Name
            : $"{row.Namespace}.{row.Name}";

    private sealed record SettingsSnapshot(
        EngineRow[] Engine,
        WorkflowRow[] Workflow);

    private sealed record EngineRow(
        long Id,
        string? Namespace,
        string Key,
        string Value,
        string CreatedAt,
        string UpdatedAt);

    private sealed record WorkflowRow(
        long Id,
        string? Namespace,
        string Name,
        string Value,
        string JsonType,
        string CreatedAt,
        string UpdatedAt);
}
