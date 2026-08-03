using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SettingDescriptionsMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration =
        "20260802163119_AddInstanceWorkflowVersionChanges";
    private const string TargetMigration =
        "20260803175244_AddSettingDescriptionsAndManagementRole";

    private static readonly string[] EngineDefaultKeys =
    [
        "Workflow.RequiredRole",
        "WorkflowInstances.RequiredRole",
        "NodeExecution.RequiredRole",
        "WorkflowJobs.RequiredRole",
        "Delegation.AdminRoles",
        "Workflow.Gateway.MaxActiveTokens",
        "Workflow.MultiInstance.MaxInstances",
        "Workflow.Async.MaxConsecutiveAutomaticActivations"
    ];

    private static readonly string[] WorkflowDefaultKeys =
    [
        "examples.messageClientId",
        "examples.messageCorrelation"
    ];

    [Fact]
    public async Task FreshDatabaseAddsNullableDescriptionColumnsAndDescribesDefaults()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);

            var columns = await ReadDescriptionColumnsAsync(connectionString);
            Assert.Equal(2, columns.Length);
            Assert.All(columns, column =>
            {
                Assert.Equal("character varying", column.DataType);
                Assert.Equal(1000, column.MaximumLength);
                Assert.True(column.IsNullable);
            });

            var settings = await ReadSettingsAsync(connectionString);
            var managementRole = Assert.Single(
                settings.Engine,
                row => LogicalEngineKey(row) == "Settings.RequiredRole");
            Assert.Equal("admin", managementRole.Value);
            Assert.False(string.IsNullOrWhiteSpace(managementRole.Description));

            Assert.Equal(9, settings.Engine.Length);
            foreach (var logicalKey in EngineDefaultKeys)
            {
                var row = Assert.Single(
                    settings.Engine,
                    candidate => LogicalEngineKey(candidate) == logicalKey);
                Assert.False(string.IsNullOrWhiteSpace(row.Description));
            }

            Assert.Equal(2, settings.Workflow.Length);
            foreach (var logicalKey in WorkflowDefaultKeys)
            {
                var row = Assert.Single(
                    settings.Workflow,
                    candidate => string.Equals(
                        LogicalWorkflowKey(candidate),
                        logicalKey,
                        StringComparison.OrdinalIgnoreCase));
                Assert.False(string.IsNullOrWhiteSpace(row.Description));
            }
        });
    }

    [Fact]
    public async Task UpgradeBackfillsCanonicalAndLegacyDefaultsWithoutChangingExistingData()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await CustomizeDefaultsAndSeedLegacyRowsAsync(connectionString);
            var before = await ReadSettingsWithoutDescriptionsAsync(connectionString);

            await MigrateAsync(connectionString, TargetMigration);

            var after = await ReadSettingsAsync(connectionString);
            foreach (var expected in before.Engine)
            {
                var actual = Assert.Single(
                    after.Engine,
                    row => row.Id == expected.Id);
                Assert.Equal(expected, actual.Core);
            }
            foreach (var expected in before.Workflow)
            {
                var actual = Assert.Single(
                    after.Workflow,
                    row => row.Id == expected.Id);
                Assert.Equal(expected, actual.Core);
            }

            var managementRole = Assert.Single(
                after.Engine,
                row => LogicalEngineKey(row) == "Settings.RequiredRole");
            Assert.Equal("operator-management-role", managementRole.Value);
            Assert.False(string.IsNullOrWhiteSpace(managementRole.Description));

            foreach (var logicalKey in EngineDefaultKeys)
            {
                var rows = after.Engine
                    .Where(row => LogicalEngineKey(row) == logicalKey)
                    .ToArray();
                Assert.Equal(2, rows.Length);
                Assert.Contains(rows, row => !string.IsNullOrWhiteSpace(row.Namespace));
                Assert.Contains(rows, row => string.IsNullOrWhiteSpace(row.Namespace));
                Assert.All(
                    rows,
                    row => Assert.False(string.IsNullOrWhiteSpace(row.Description)));
            }

            foreach (var logicalKey in WorkflowDefaultKeys)
            {
                var rows = after.Workflow
                    .Where(row => string.Equals(
                        LogicalWorkflowKey(row),
                        logicalKey,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert.Equal(2, rows.Length);
                Assert.Contains(rows, row => !string.IsNullOrWhiteSpace(row.Namespace));
                Assert.Contains(rows, row => string.IsNullOrWhiteSpace(row.Namespace));
                Assert.All(
                    rows,
                    row => Assert.False(string.IsNullOrWhiteSpace(row.Description)));
            }

            Assert.Null(Assert.Single(
                after.Engine,
                row => LogicalEngineKey(row) == "Operator.Custom").Description);
            Assert.Null(Assert.Single(
                after.Workflow,
                row => LogicalWorkflowKey(row) == "operator.customPayload").Description);
        });
    }

    [Fact]
    public async Task DownDropsDescriptionColumnsButPreservesManagementRole()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);
            var before = await ReadSettingsAsync(connectionString);
            var expectedRole = Assert.Single(
                before.Engine,
                row => LogicalEngineKey(row) == "Settings.RequiredRole").Core;

            await MigrateAsync(connectionString, PreviousMigration);

            Assert.Empty(await ReadDescriptionColumnsAsync(connectionString));
            var after = await ReadSettingsWithoutDescriptionsAsync(connectionString);
            var actualRole = Assert.Single(
                after.Engine,
                row => LogicalEngineKey(row) == "Settings.RequiredRole");
            Assert.Equal(expectedRole, actualRole);
        });
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "setting_descriptions_" + Guid.NewGuid().ToString("N");
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

    private static async Task CustomizeDefaultsAndSeedLegacyRowsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE flowbit.engine_settings
            SET "Value" = 'canonical:' || "Namespace" || '.' || "Key",
                "CreatedAt" = '2026-03-01T00:00:00Z'::timestamptz
                              + "Id" * INTERVAL '1 second',
                "UpdatedAt" = '2026-03-02T00:00:00Z'::timestamptz
                              + "Id" * INTERVAL '1 second';

            UPDATE flowbit.workflow_settings
            SET "Value" = jsonb_build_object(
                    'source', 'canonical',
                    'logicalName', "Namespace" || '.' || "Name",
                    'id', "Id"),
                "CreatedAt" = '2026-03-03T00:00:00Z'::timestamptz
                              + "Id" * INTERVAL '1 second',
                "UpdatedAt" = '2026-03-04T00:00:00Z'::timestamptz
                              + "Id" * INTERVAL '1 second';

            INSERT INTO flowbit.engine_settings
                ("Namespace", "Key", "Value", "CreatedAt", "UpdatedAt")
            VALUES
                ('   ', 'Settings.RequiredRole', 'operator-management-role',
                 '2026-04-01T00:00:00Z', '2026-04-02T00:00:00Z'),
                (NULL, 'Workflow.RequiredRole', 'legacy-workflow-role',
                 '2026-04-01T00:00:01Z', '2026-04-02T00:00:01Z'),
                ('   ', 'WorkflowInstances.RequiredRole', 'legacy-instance-role',
                 '2026-04-01T00:00:02Z', '2026-04-02T00:00:02Z'),
                (NULL, 'NodeExecution.RequiredRole', 'legacy-node-role',
                 '2026-04-01T00:00:03Z', '2026-04-02T00:00:03Z'),
                ('', 'WorkflowJobs.RequiredRole', 'legacy-job-role',
                 '2026-04-01T00:00:04Z', '2026-04-02T00:00:04Z'),
                (NULL, 'Delegation.AdminRoles', 'legacy-delegation-role',
                 '2026-04-01T00:00:05Z', '2026-04-02T00:00:05Z'),
                ('', 'Workflow.Gateway.MaxActiveTokens', '321',
                 '2026-04-01T00:00:06Z', '2026-04-02T00:00:06Z'),
                (NULL, 'Workflow.MultiInstance.MaxInstances', '654',
                 '2026-04-01T00:00:07Z', '2026-04-02T00:00:07Z'),
                ('', 'Workflow.Async.MaxConsecutiveAutomaticActivations', '987',
                 '2026-04-01T00:00:08Z', '2026-04-02T00:00:08Z'),
                ('Operator', 'Custom', 'operator-owned',
                 '2026-04-01T00:00:09Z', '2026-04-02T00:00:09Z');

            INSERT INTO flowbit.workflow_settings
                ("Namespace", "Name", "Value", "CreatedAt", "UpdatedAt")
            VALUES
                (NULL, 'EXAMPLES.messageClientId',
                 '{"source":"legacy","sequence":1}'::jsonb,
                 '2026-04-03T00:00:01Z', '2026-04-04T00:00:01Z'),
                ('   ', 'examples.MESSAGECORRELATION',
                 '["legacy",2]'::jsonb,
                 '2026-04-03T00:00:02Z', '2026-04-04T00:00:02Z'),
                ('operator', 'customPayload',
                 '{"enabled":true,"threshold":12.5}'::jsonb,
                 '2026-04-03T00:00:03Z', '2026-04-04T00:00:03Z');
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SettingsSnapshot> ReadSettingsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var engine = new List<EngineRow>();
        await using (var command = new NpgsqlCommand("""
            SELECT "Id", "Namespace", "Key", "Value", "Description",
                   "CreatedAt"::text, "UpdatedAt"::text
            FROM flowbit.engine_settings
            ORDER BY "Id"
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
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
        }

        var workflow = new List<WorkflowRow>();
        await using (var command = new NpgsqlCommand("""
            SELECT "Id", "Namespace", "Name", "Value"::text, "Description",
                   "CreatedAt"::text, "UpdatedAt"::text
            FROM flowbit.workflow_settings
            ORDER BY "Id"
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
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
        }

        return new SettingsSnapshot(engine.ToArray(), workflow.ToArray());
    }

    private static async Task<SettingsWithoutDescriptions>
        ReadSettingsWithoutDescriptionsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var engine = new List<EngineCore>();
        await using (var command = new NpgsqlCommand("""
            SELECT "Id", "Namespace", "Key", "Value",
                   "CreatedAt"::text, "UpdatedAt"::text
            FROM flowbit.engine_settings
            ORDER BY "Id"
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                engine.Add(new EngineCore(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            }
        }

        var workflow = new List<WorkflowCore>();
        await using (var command = new NpgsqlCommand("""
            SELECT "Id", "Namespace", "Name", "Value"::text,
                   "CreatedAt"::text, "UpdatedAt"::text
            FROM flowbit.workflow_settings
            ORDER BY "Id"
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                workflow.Add(new WorkflowCore(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            }
        }

        return new SettingsWithoutDescriptions(
            engine.ToArray(),
            workflow.ToArray());
    }

    private static async Task<DescriptionColumn[]> ReadDescriptionColumnsAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT table_name, data_type, character_maximum_length,
                   is_nullable = 'YES'
            FROM information_schema.columns
            WHERE table_schema = 'flowbit'
              AND table_name IN ('engine_settings', 'workflow_settings')
              AND column_name = 'Description'
            ORDER BY table_name
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<DescriptionColumn>();
        while (await reader.ReadAsync())
        {
            columns.Add(new DescriptionColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetBoolean(3)));
        }
        return columns.ToArray();
    }

    private static string LogicalEngineKey(EngineRow row) =>
        string.IsNullOrWhiteSpace(row.Namespace)
            ? row.Key
            : $"{row.Namespace.Trim()}.{row.Key}";

    private static string LogicalEngineKey(EngineCore row) =>
        string.IsNullOrWhiteSpace(row.Namespace)
            ? row.Key
            : $"{row.Namespace.Trim()}.{row.Key}";

    private static string LogicalWorkflowKey(WorkflowRow row) =>
        string.IsNullOrWhiteSpace(row.Namespace)
            ? row.Name
            : $"{row.Namespace.Trim()}.{row.Name}";

    private sealed record SettingsSnapshot(
        EngineRow[] Engine,
        WorkflowRow[] Workflow);

    private sealed record SettingsWithoutDescriptions(
        EngineCore[] Engine,
        WorkflowCore[] Workflow);

    private sealed record EngineRow(
        long Id,
        string? Namespace,
        string Key,
        string Value,
        string? Description,
        string CreatedAt,
        string UpdatedAt)
    {
        public EngineCore Core => new(
            Id,
            Namespace,
            Key,
            Value,
            CreatedAt,
            UpdatedAt);
    }

    private sealed record EngineCore(
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
        string? Description,
        string CreatedAt,
        string UpdatedAt)
    {
        public WorkflowCore Core => new(
            Id,
            Namespace,
            Name,
            Value,
            CreatedAt,
            UpdatedAt);
    }

    private sealed record WorkflowCore(
        long Id,
        string? Namespace,
        string Name,
        string Value,
        string CreatedAt,
        string UpdatedAt);

    private sealed record DescriptionColumn(
        string TableName,
        string DataType,
        int MaximumLength,
        bool IsNullable);
}
