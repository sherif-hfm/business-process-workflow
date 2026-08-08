using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVersionChangeBatchMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration =
        "20260808120000_RepairPositionFirstAdministrativeActionBatches";
    private const string InitialBatchMigration =
        "20260808124633_AddInstanceVersionChangeBatches";
    private const string TargetMigration =
        "20260808133917_AddInstanceVersionChangeBatchClassificationCounts";

    [Fact]
    public async Task FreshMigrationCreatesDedicatedSchemaConstraintsIndexesAndSetting()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using (var tables = new NpgsqlCommand(
                """
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = 'flowbit'
                  AND table_name IN (
                      'workflow_instance_version_change_batches',
                      'workflow_instance_version_change_batch_items')
                ORDER BY table_name
                """,
                connection))
            await using (var reader = await tables.ExecuteReaderAsync())
            {
                var names = new List<string>();
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(0));
                }

                Assert.Equal(
                    [
                        "workflow_instance_version_change_batch_items",
                        "workflow_instance_version_change_batches"
                    ],
                    names);
            }

            var constraints = await ReadCatalogDefinitionsAsync(
                connection,
                """
                SELECT conname, pg_get_constraintdef(oid)
                FROM pg_catalog.pg_constraint
                WHERE connamespace = 'flowbit'::regnamespace
                  AND conrelid IN (
                      'flowbit.workflow_instance_version_change_batches'::regclass,
                      'flowbit.workflow_instance_version_change_batch_items'::regclass,
                      'flowbit.workflow_instance_version_changes'::regclass)
                ORDER BY conname
                """);

            Assert.Contains(constraints, constraint =>
                constraint.Name == "CK_workflow_instance_version_change_batches_counts"
                && constraint.Definition.Contains(
                    "\"TotalItemCount\" <= 10000",
                    StringComparison.Ordinal)
                && constraint.Definition.Contains(
                    "\"BlockedItemCount\" <= \"IneligibleItemCount\"",
                    StringComparison.Ordinal)
                && constraint.Definition.Contains(
                    "\"StaleItemCount\" <= (\"IneligibleItemCount\" + \"SkippedItemCount\")",
                    StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Name == "CK_workflow_instance_version_change_batches_status"
                && constraint.Definition.Contains("completedWithIssues", StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Name == "CK_workflow_instance_version_change_batch_items_status"
                && constraint.Definition.Contains("'eligible'", StringComparison.Ordinal)
                && constraint.Definition.Contains("'failed'", StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Name == "CK_workflow_instance_version_changes_batch_correlation"
                && constraint.Definition.Contains("BatchItemId", StringComparison.Ordinal));

            var batchDefinitionForeignKeys = constraints
                .Where(constraint =>
                    constraint.Definition.Contains(
                        "REFERENCES flowbit.workflow_definitions",
                        StringComparison.Ordinal)
                    && constraint.Name.Contains(
                        "workflow_instance_version_change_batches",
                        StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, batchDefinitionForeignKeys.Length);
            Assert.All(batchDefinitionForeignKeys, constraint =>
                Assert.Contains("ON DELETE RESTRICT", constraint.Definition, StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Definition.Contains(
                    "REFERENCES flowbit.workflow_instances",
                    StringComparison.Ordinal)
                && constraint.Definition.Contains("ON DELETE RESTRICT", StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Definition.Contains(
                    "FOREIGN KEY (\"CapturedSourceWorkflowDefinitionId\")",
                    StringComparison.Ordinal)
                && constraint.Definition.Contains("ON DELETE RESTRICT", StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Definition.Contains(
                    "REFERENCES flowbit.workflow_instance_version_change_batches",
                    StringComparison.Ordinal)
                && constraint.Definition.Contains("ON DELETE CASCADE", StringComparison.Ordinal));

            var indexes = await ReadCatalogDefinitionsAsync(
                connection,
                """
                SELECT indexname, indexdef
                FROM pg_catalog.pg_indexes
                WHERE schemaname = 'flowbit'
                  AND tablename IN (
                      'workflow_instance_version_change_batches',
                      'workflow_instance_version_change_batch_items',
                      'workflow_instance_version_changes')
                ORDER BY indexname
                """);
            Assert.Contains(indexes, index =>
                index.Definition.Contains(
                    "(\"Status\", \"UpdatedAt\", \"Id\")",
                    StringComparison.Ordinal));
            Assert.Contains(indexes, index =>
                index.Definition.Contains(
                    "(\"WorkflowKey\", \"Status\", \"UpdatedAt\", \"Id\")",
                    StringComparison.Ordinal));
            Assert.Contains(indexes, index =>
                index.Definition.Contains(
                    "(\"BatchId\", \"Status\", \"Id\")",
                    StringComparison.Ordinal));
            Assert.Contains(indexes, index =>
                index.Definition.Contains(
                    "UNIQUE INDEX",
                    StringComparison.Ordinal)
                && index.Definition.Contains(
                    "(\"BatchId\", \"InstanceId\")",
                    StringComparison.Ordinal));
            Assert.Contains(indexes, index =>
                index.Definition.Contains(
                    "UNIQUE INDEX",
                    StringComparison.Ordinal)
                && index.Definition.Contains("(\"BatchItemId\")", StringComparison.Ordinal)
                && index.Definition.Contains(
                    "\"BatchItemId\" IS NOT NULL",
                    StringComparison.Ordinal));

            await using var setting = new NpgsqlCommand(
                """
                SELECT "Value", "Description"
                FROM flowbit.engine_settings
                WHERE "Namespace" = 'WorkflowVersionChanges'
                  AND "Key" = 'MaxBatchInstances'
                """,
                connection);
            await using var settingReader = await setting.ExecuteReaderAsync();
            Assert.True(await settingReader.ReadAsync());
            Assert.Equal("10000", settingReader.GetString(0));
            Assert.False(settingReader.IsDBNull(1));
            Assert.False(string.IsNullOrWhiteSpace(settingReader.GetString(1)));
            Assert.False(await settingReader.ReadAsync());
        });
    }

    [Fact]
    public async Task ExistingInitialBatchSchemaUpgradesAdditivelyToClassificationCounts()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, InitialBatchMigration);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();

                await using (var initialColumn = new NpgsqlCommand(
                    """
                    SELECT count(*)
                    FROM information_schema.columns
                    WHERE table_schema = 'flowbit'
                      AND table_name = 'workflow_instance_version_change_batches'
                      AND column_name = 'BlockedItemCount'
                    """,
                    connection))
                {
                    Assert.Equal(0L, (long)(await initialColumn.ExecuteScalarAsync())!);
                }

                var initialConstraints = await ReadCatalogDefinitionsAsync(
                    connection,
                    """
                    SELECT conname, pg_get_constraintdef(oid)
                    FROM pg_catalog.pg_constraint
                    WHERE connamespace = 'flowbit'::regnamespace
                      AND conrelid =
                          'flowbit.workflow_instance_version_change_batches'::regclass
                      AND conname =
                          'CK_workflow_instance_version_change_batches_counts'
                    """);
                var initialCounts = Assert.Single(initialConstraints);
                Assert.DoesNotContain(
                    "BlockedItemCount",
                    initialCounts.Definition,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "\"StaleItemCount\" <= \"IneligibleItemCount\"",
                    initialCounts.Definition,
                    StringComparison.Ordinal);

                await using var seed = new NpgsqlCommand(
                    """
                    WITH source_definition AS (
                        INSERT INTO flowbit.workflow_definitions
                            ("Name", "WorkflowKey", "Version", "Definition",
                             "IsPublished", "IsDefault", "CreatedAt")
                        VALUES
                            ('Batch migration source', 'batch-migration-upgrade', 1,
                             '{"id":"batch-migration-upgrade","name":"Batch migration source","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}'::jsonb,
                             true, false, clock_timestamp())
                        RETURNING "Id"
                    ),
                    target_definition AS (
                        INSERT INTO flowbit.workflow_definitions
                            ("Name", "WorkflowKey", "Version", "Definition",
                             "IsPublished", "IsDefault", "CreatedAt")
                        VALUES
                            ('Batch migration target', 'batch-migration-upgrade', 2,
                             '{"id":"batch-migration-upgrade","name":"Batch migration target","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}'::jsonb,
                             true, false, clock_timestamp())
                        RETURNING "Id"
                    ),
                    instance_rows AS (
                        INSERT INTO flowbit.workflow_instances
                            ("WorkflowDefinitionId", "WorkflowKey", "Status",
                             "StartedBy", "CreatedAt", "UpdatedAt")
                        SELECT source_definition."Id", 'batch-migration-upgrade',
                               'running', 'migration-test', clock_timestamp(),
                               clock_timestamp()
                        FROM source_definition
                        CROSS JOIN generate_series(1, 3)
                        RETURNING "Id", "UpdatedAt"
                    ),
                    batch_row AS (
                        INSERT INTO flowbit.workflow_instance_version_change_batches
                            ("WorkflowKey", "SourceWorkflowDefinitionId",
                             "TargetWorkflowDefinitionId", "Reason", "SelectionJson",
                             "Status", "PreparedBy", "TotalItemCount",
                             "EligibleItemCount", "IneligibleItemCount",
                             "WarningItemCount", "StaleItemCount", "QueuedItemCount",
                             "SucceededItemCount", "SkippedItemCount", "FailedItemCount",
                             "CancelledItemCount")
                        SELECT 'batch-migration-upgrade', source_definition."Id",
                               target_definition."Id", 'Verify additive schema upgrade.',
                               '{"mode":"explicit","instanceIds":[]}'::jsonb,
                               'completedWithIssues', 'migration-test', 3, 0, 2,
                               0, 1, 0, 0, 1, 0, 0
                        FROM source_definition, target_definition
                        RETURNING "Id", "SourceWorkflowDefinitionId"
                    ),
                    numbered_instances AS (
                        SELECT "Id", "UpdatedAt",
                               row_number() OVER (ORDER BY "Id") AS ordinal
                        FROM instance_rows
                    )
                    INSERT INTO flowbit.workflow_instance_version_change_batch_items
                        ("BatchId", "InstanceId", "CapturedSourceWorkflowDefinitionId",
                         "CapturedInstanceUpdatedAt", "Status", "ErrorCode")
                    SELECT batch_row."Id", numbered_instances."Id",
                           batch_row."SourceWorkflowDefinitionId",
                           numbered_instances."UpdatedAt",
                           CASE WHEN numbered_instances.ordinal = 3
                                THEN 'skipped' ELSE 'ineligible' END,
                           CASE numbered_instances.ordinal
                               WHEN 1 THEN 'incompatible'
                               WHEN 2 THEN 'stale_since_selection'
                               ELSE 'stale_since_preparation'
                           END
                    FROM batch_row, numbered_instances
                    RETURNING "Id"
                    """,
                    connection);
                Assert.True((long)(await seed.ExecuteScalarAsync())! > 0);
            }

            await MigrateAsync(connectionString, TargetMigration);

            await using (var upgraded = new NpgsqlConnection(connectionString))
            {
                await upgraded.OpenAsync();
                await using (var batch = new NpgsqlCommand(
                    """
                    SELECT "BlockedItemCount", "StaleItemCount",
                           "IneligibleItemCount", "SkippedItemCount"
                    FROM flowbit.workflow_instance_version_change_batches
                    WHERE "WorkflowKey" = 'batch-migration-upgrade'
                    """,
                    upgraded))
                await using (var batchReader = await batch.ExecuteReaderAsync())
                {
                    Assert.True(await batchReader.ReadAsync());
                    Assert.Equal(1, batchReader.GetInt32(0));
                    Assert.Equal(2, batchReader.GetInt32(1));
                    Assert.Equal(2, batchReader.GetInt32(2));
                    Assert.Equal(1, batchReader.GetInt32(3));
                    Assert.False(await batchReader.ReadAsync());
                }

                var upgradedConstraints = await ReadCatalogDefinitionsAsync(
                    upgraded,
                    """
                    SELECT conname, pg_get_constraintdef(oid)
                    FROM pg_catalog.pg_constraint
                    WHERE connamespace = 'flowbit'::regnamespace
                      AND conrelid =
                          'flowbit.workflow_instance_version_change_batches'::regclass
                      AND conname =
                          'CK_workflow_instance_version_change_batches_counts'
                    """);
                var upgradedCounts = Assert.Single(upgradedConstraints);
                Assert.Contains(
                    "\"BlockedItemCount\" <= \"IneligibleItemCount\"",
                    upgradedCounts.Definition,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "\"StaleItemCount\" <= (\"IneligibleItemCount\" + \"SkippedItemCount\")",
                    upgradedCounts.Definition,
                    StringComparison.Ordinal);

                await using var history = new NpgsqlCommand(
                    """
                    SELECT "MigrationId"
                    FROM flowbit."__EFMigrationsHistory"
                    WHERE "MigrationId" IN (
                        '20260808124633_AddInstanceVersionChangeBatches',
                        '20260808133917_AddInstanceVersionChangeBatchClassificationCounts')
                    ORDER BY "MigrationId"
                    """,
                    upgraded);
                await using var reader = await history.ExecuteReaderAsync();
                var applied = new List<string>();
                while (await reader.ReadAsync())
                {
                    applied.Add(reader.GetString(0));
                }

                Assert.Equal([InitialBatchMigration, TargetMigration], applied);
            }

            await MigrateAsync(connectionString, InitialBatchMigration);

            await using var downgraded = new NpgsqlConnection(connectionString);
            await downgraded.OpenAsync();
            await using var downgradedBatch = new NpgsqlCommand(
                """
                SELECT "StaleItemCount",
                       (SELECT count(*)
                        FROM information_schema.columns
                        WHERE table_schema = 'flowbit'
                          AND table_name =
                              'workflow_instance_version_change_batches'
                          AND column_name = 'BlockedItemCount')
                FROM flowbit.workflow_instance_version_change_batches
                WHERE "WorkflowKey" = 'batch-migration-upgrade'
                """,
                downgraded);
            await using var downgradedReader = await downgradedBatch.ExecuteReaderAsync();
            Assert.True(await downgradedReader.ReadAsync());
            Assert.Equal(1, downgradedReader.GetInt32(0));
            Assert.Equal(0L, downgradedReader.GetInt64(1));
            Assert.False(await downgradedReader.ReadAsync());
        });
    }

    [Fact]
    public async Task MigrationPreservesExistingSettingAndLeavesLegacyAuditUncorrelated()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var auditId = await SeedLegacyAuditAndCustomSettingAsync(connectionString);

            await MigrateAsync(connectionString, TargetMigration);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT
                    change."BatchId",
                    change."BatchItemId",
                    setting."Value",
                    setting."Description"
                FROM flowbit.workflow_instance_version_changes AS change
                CROSS JOIN flowbit.engine_settings AS setting
                WHERE change."Id" = @auditId
                  AND setting."Namespace" = 'WorkflowVersionChanges'
                  AND setting."Key" = 'MaxBatchInstances'
                """,
                connection);
            command.Parameters.AddWithValue("auditId", auditId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal("4321", reader.GetString(2));
            Assert.Equal("Operator-defined limit", reader.GetString(3));
            Assert.False(await reader.ReadAsync());
        });
    }

    private static async Task<long> SeedLegacyAuditAndCustomSettingAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO flowbit.engine_settings
                ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
            VALUES
                ('WorkflowVersionChanges', 'MaxBatchInstances', '4321',
                 'Operator-defined limit', clock_timestamp(), clock_timestamp());

            WITH source_definition AS (
                INSERT INTO flowbit.workflow_definitions
                    ("Name", "WorkflowKey", "Version", "Definition",
                     "IsPublished", "IsDefault", "CreatedAt")
                VALUES
                    ('Legacy source', 'legacy-batch-audit', 1,
                     '{"id":"legacy-batch-audit","name":"Legacy source","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}'::jsonb,
                     true, false, TIMESTAMPTZ '2026-01-01 00:00:00+00')
                RETURNING "Id"
            ),
            target_definition AS (
                INSERT INTO flowbit.workflow_definitions
                    ("Name", "WorkflowKey", "Version", "Definition",
                     "IsPublished", "IsDefault", "CreatedAt")
                VALUES
                    ('Legacy target', 'legacy-batch-audit', 2,
                     '{"id":"legacy-batch-audit","name":"Legacy target","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}'::jsonb,
                     true, false, TIMESTAMPTZ '2026-01-02 00:00:00+00')
                RETURNING "Id"
            ),
            instance_row AS (
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy",
                     "CreatedAt", "UpdatedAt")
                SELECT "Id", 'legacy-batch-audit', 'running', 'legacy-admin',
                       TIMESTAMPTZ '2026-01-02 00:00:00+00',
                       TIMESTAMPTZ '2026-01-03 00:00:00+00'
                FROM target_definition
                RETURNING "Id"
            )
            INSERT INTO flowbit.workflow_instance_version_changes
                ("InstanceId", "SourceWorkflowDefinitionId", "TargetWorkflowDefinitionId",
                 "ChangedBy", "ChangedByRolesJson", "Reason", "ChangedAt")
            SELECT instance_row."Id", source_definition."Id", target_definition."Id",
                   'legacy-admin', '["admin"]'::jsonb,
                   'Legacy direct change before batch support.',
                   TIMESTAMPTZ '2026-01-03 00:00:00+00'
            FROM instance_row, source_definition, target_definition
            RETURNING "Id";
            """,
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<CatalogDefinition>> ReadCatalogDefinitionsAsync(
        NpgsqlConnection connection,
        string sql)
    {
        var result = new List<CatalogDefinition>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new CatalogDefinition(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "instance_version_batches_" + Guid.NewGuid().ToString("N");
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

    private sealed record CatalogDefinition(string Name, string Definition);
}
