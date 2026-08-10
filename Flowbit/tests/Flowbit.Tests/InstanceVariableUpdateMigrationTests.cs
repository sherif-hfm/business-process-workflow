using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVariableUpdateMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration =
        "20260808133917_AddInstanceVersionChangeBatchClassificationCounts";
    private const string TargetMigration =
        "20260810174726_AddInstanceVariableUpdates";

    [Fact]
    public async Task FreshMigrationCreatesAuditBatchJobLinksConstraintsIndexesAndSetting()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            var tables = await ReadNamesAsync(
                connection,
                """
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = 'flowbit'
                  AND table_name IN (
                      'instance_variable_updates',
                      'instance_variable_update_batches',
                      'instance_variable_update_batch_items',
                      'instance_variable_update_batch_jobs')
                ORDER BY table_name
                """);
            Assert.Equal(
                [
                    "instance_variable_update_batch_items",
                    "instance_variable_update_batch_jobs",
                    "instance_variable_update_batches",
                    "instance_variable_updates"
                ],
                tables);

            await using (var column = new NpgsqlCommand(
                """
                SELECT is_nullable, data_type
                FROM information_schema.columns
                WHERE table_schema = 'flowbit'
                  AND table_name = 'instance_variables'
                  AND column_name = 'InstanceVariableUpdateAuditId'
                """,
                connection))
            await using (var reader = await column.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("YES", reader.GetString(0));
                Assert.Equal("bigint", reader.GetString(1));
                Assert.False(await reader.ReadAsync());
            }

            await using (var redundantColumn = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM information_schema.columns
                WHERE table_schema = 'flowbit'
                  AND table_name = 'instance_variable_update_batch_items'
                  AND column_name = 'UpdateOperationId'
                """,
                connection))
            {
                Assert.Equal(0L, await redundantColumn.ExecuteScalarAsync());
            }

            var constraints = await ReadDefinitionsAsync(
                connection,
                """
                SELECT conname, pg_get_constraintdef(oid)
                FROM pg_catalog.pg_constraint
                WHERE connamespace = 'flowbit'::regnamespace
                  AND conrelid IN (
                      'flowbit.instance_variable_updates'::regclass,
                      'flowbit.instance_variable_update_batches'::regclass,
                      'flowbit.instance_variable_update_batch_items'::regclass,
                      'flowbit.instance_variable_update_batch_jobs'::regclass,
                      'flowbit.instance_variables'::regclass)
                ORDER BY conname
                """);
            Assert.Contains(constraints, constraint =>
                constraint.Name == "CK_instance_variable_update_batches_counts"
                && constraint.Definition.Contains(
                    "\"TotalItemCount\" <= 10000",
                    StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Name == "CK_instance_variable_update_batches_variables"
                && constraint.Definition.Contains(
                    "jsonb_array_length(\"VariablesJson\") >= 1",
                    StringComparison.Ordinal)
                && constraint.Definition.Contains(
                    "jsonb_array_length(\"VariablesJson\") <= 100",
                    StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Name == "CK_instance_variable_update_batch_jobs_phase"
                && constraint.Definition.Contains("'prepare'", StringComparison.Ordinal)
                && constraint.Definition.Contains("'execute'", StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Name == "CK_instance_variable_updates_batch_correlation"
                && constraint.Definition.Contains("BatchItemId", StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Name == "CK_instance_variable_updates_requested_variables"
                && constraint.Definition.Contains("= 'array'", StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Definition.Contains(
                    "FOREIGN KEY (\"JobId\")",
                    StringComparison.Ordinal)
                && constraint.Definition.Contains(
                    "ON DELETE SET NULL",
                    StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Definition.Contains(
                    "FOREIGN KEY (\"InstanceVariableUpdateAuditId\", \"InstanceId\")",
                    StringComparison.Ordinal)
                && constraint.Definition.Contains(
                    "ON DELETE RESTRICT",
                    StringComparison.Ordinal));
            Assert.Contains(constraints, constraint =>
                constraint.Definition.Contains(
                    "FOREIGN KEY (\"BatchItemId\", \"BatchId\", \"InstanceId\")",
                    StringComparison.Ordinal)
                && constraint.Definition.Contains(
                    "REFERENCES flowbit.instance_variable_update_batch_items(\"Id\", \"BatchId\", \"InstanceId\")",
                    StringComparison.Ordinal));

            var indexes = await ReadDefinitionsAsync(
                connection,
                """
                SELECT indexname, indexdef
                FROM pg_catalog.pg_indexes
                WHERE schemaname = 'flowbit'
                  AND tablename IN (
                      'instance_variable_updates',
                      'instance_variable_update_batches',
                      'instance_variable_update_batch_items',
                      'instance_variable_update_batch_jobs',
                      'instance_variables')
                ORDER BY indexname
                """);
            Assert.Contains(indexes, index =>
                index.Definition.Contains("UNIQUE INDEX", StringComparison.Ordinal)
                && index.Definition.Contains(
                    "(\"BatchId\", \"InstanceId\")",
                    StringComparison.Ordinal));
            Assert.Contains(indexes, index =>
                index.Definition.Contains("UNIQUE INDEX", StringComparison.Ordinal)
                && index.Definition.Contains(
                    "(\"BatchId\", \"WorkflowDefinitionId\", \"Phase\")",
                    StringComparison.Ordinal));
            Assert.Contains(indexes, index =>
                index.Definition.Contains("UNIQUE INDEX", StringComparison.Ordinal)
                && index.Definition.Contains("(\"OriginalJobId\")", StringComparison.Ordinal));
            Assert.Contains(indexes, index =>
                index.Definition.Contains("UNIQUE INDEX", StringComparison.Ordinal)
                && index.Definition.Contains(
                    "(\"InstanceId\", \"PerformedBy\", \"IdempotencyKey\")",
                    StringComparison.Ordinal)
                && index.Definition.Contains(
                    "\"IdempotencyKey\" IS NOT NULL",
                    StringComparison.Ordinal));
            Assert.Contains(indexes, index =>
                index.Definition.Contains(
                    "(\"InstanceVariableUpdateAuditId\", \"InstanceId\")",
                    StringComparison.Ordinal)
                && index.Definition.Contains(
                    "\"InstanceVariableUpdateAuditId\" IS NOT NULL",
                    StringComparison.Ordinal));
            Assert.Contains(indexes, index =>
                index.Definition.Contains("UNIQUE INDEX", StringComparison.Ordinal)
                && index.Definition.Contains(
                    "(\"BatchItemId\", \"BatchId\", \"InstanceId\")",
                    StringComparison.Ordinal));

            await using (var trigger = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_catalog.pg_trigger
                WHERE tgrelid = 'flowbit.instance_variable_update_batch_jobs'::regclass
                  AND tgname = 'TR_instance_variable_update_batch_jobs_validate_live_job'
                  AND NOT tgisinternal
                """,
                connection))
            {
                Assert.Equal(1L, await trigger.ExecuteScalarAsync());
            }

            await using var setting = new NpgsqlCommand(
                """
                SELECT "Value", "Description"
                FROM flowbit.engine_settings
                WHERE "Namespace" = 'WorkflowVariableUpdates'
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
    public async Task UpgradeAndDowngradePreserveOperatorSettingAndRemoveOnlyFeatureSchema()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand(
                    """
                    INSERT INTO flowbit.engine_settings
                        ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                    VALUES
                        ('WorkflowVariableUpdates', 'MaxBatchInstances', '4321',
                         'Operator override', clock_timestamp(), clock_timestamp())
                    """,
                    connection);
                Assert.Equal(1, await seed.ExecuteNonQueryAsync());
            }

            await MigrateAsync(connectionString, TargetMigration);
            await AssertSettingAsync(connectionString, "4321", "Operator override");

            await MigrateAsync(connectionString, PreviousMigration);
            await AssertSettingAsync(connectionString, "4321", "Operator override");

            await using var downgraded = new NpgsqlConnection(connectionString);
            await downgraded.OpenAsync();
            await using var schema = new NpgsqlCommand(
                """
                SELECT
                    (SELECT count(*)
                     FROM information_schema.tables
                     WHERE table_schema = 'flowbit'
                       AND table_name LIKE 'instance_variable_update%'),
                    (SELECT count(*)
                     FROM information_schema.columns
                     WHERE table_schema = 'flowbit'
                       AND table_name = 'instance_variables'
                       AND column_name = 'InstanceVariableUpdateAuditId')
                """,
                downgraded);
            await using var reader = await schema.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
        });
    }

    [Fact]
    public async Task DatabaseRejectsCrossedAuditOwnersAndLiveJobDefinitionLinks()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.EnableDynamicJson();
            await using var dataSource = dataSourceBuilder.Build();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
                .Options;
            await using var db = new AppDbContext(options);
            var now = DateTimeOffset.UtcNow;
            var workflowKey = $"variable-update-integrity-{Guid.NewGuid():N}";
            var firstDefinition = NewDefinition(workflowKey, 1);
            var secondDefinition = NewDefinition(workflowKey, 2);
            db.WorkflowDefinitions.AddRange(firstDefinition, secondDefinition);
            await db.SaveChangesAsync();

            var firstInstance = NewInstance(firstDefinition, now);
            var secondInstance = NewInstance(secondDefinition, now.AddSeconds(1));
            db.WorkflowInstances.AddRange(firstInstance, secondInstance);
            await db.SaveChangesAsync();

            var batchRepository = new InstanceVariableUpdateBatchRepository(db);
            var batch = await batchRepository.AddAsync(
                new NewInstanceVariableUpdateBatchRecord(
                    workflowKey,
                    JsonSerializer.SerializeToElement(new[]
                    {
                        new { name = "priority", value = 7 }
                    }),
                    JsonSerializer.SerializeToElement(new
                    {
                        mode = "explicit",
                        instanceIds = new[] { firstInstance.Id }
                    }),
                    "Integrity test",
                    "admin",
                    ["admin"],
                    IdempotencyKey: null,
                    now),
                CancellationToken.None);
            await batchRepository.AddItemsAsync(
                batch.Id,
                [
                    new NewInstanceVariableUpdateBatchItemRecord(
                        firstInstance.Id,
                        firstDefinition.Id,
                        firstInstance.UpdatedAt,
                        now)
                ],
                CancellationToken.None);
            var item = Assert.Single(await batchRepository.ListItemsForProcessingAsync(
                batch.Id,
                firstDefinition.Id,
                [InstanceVariableUpdateBatchItemStatuses.Preparing],
                afterItemId: null,
                take: 10,
                CancellationToken.None));

            var updateRepository = new InstanceVariableUpdateRepository(db);
            var validAudit = await updateRepository.AddAsync(
                new NewInstanceVariableUpdateAuditRecord(
                    firstInstance.Id,
                    firstDefinition.Id,
                    "admin",
                    ["admin"],
                    Reason: null,
                    JsonSerializer.SerializeToElement(new[]
                    {
                        new { name = "priority", value = 7 }
                    }),
                    IdempotencyKey: null,
                    BatchId: null,
                    BatchItemId: null,
                    now),
                CancellationToken.None);

            db.InstanceVariables.Add(new InstanceVariableEntity
            {
                InstanceId = secondInstance.Id,
                InstanceVariableUpdateAuditId = validAudit.Id,
                VariableName = "crossed-history",
                ValueJson = JsonDocument.Parse("true"),
                SetBy = "admin",
                SetAt = now
            });
            await AssertForeignKeyViolationAsync(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            await AssertForeignKeyViolationAsync(() =>
                new InstanceVariableUpdateRepository(db).AddAsync(
                    new NewInstanceVariableUpdateAuditRecord(
                        secondInstance.Id,
                        secondDefinition.Id,
                        "admin",
                        ["admin"],
                        Reason: null,
                        JsonSerializer.SerializeToElement(new[]
                        {
                            new { name = "priority", value = 8 }
                        }),
                        IdempotencyKey: null,
                        batch.Id,
                        item.Id,
                        now),
                    CancellationToken.None));
            db.ChangeTracker.Clear();

            var secondDefinitionJob = NewJob(secondDefinition, workflowKey, now);
            db.WorkflowJobs.Add(secondDefinitionJob);
            await db.SaveChangesAsync();
            await AssertForeignKeyViolationAsync(() =>
                new InstanceVariableUpdateBatchRepository(db).AddJobLinkAsync(
                    new NewInstanceVariableUpdateBatchJobLinkRecord(
                        batch.Id,
                        firstDefinition.Id,
                        InstanceVariableUpdateBatchPhases.Prepare,
                        secondDefinitionJob.Id,
                        secondDefinitionJob.Id),
                    CancellationToken.None));
        });
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "instance_variable_updates_" + Guid.NewGuid().ToString("N");
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
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task AssertForeignKeyViolationAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(action);
        var postgres = exception.InnerException as PostgresException;
        Assert.NotNull(postgres);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
    }

    private static WorkflowDefinitionEntity NewDefinition(
        string workflowKey,
        int version) =>
        new()
        {
            Name = $"Variable update integrity v{version}",
            WorkflowKey = workflowKey,
            Version = version,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = $"Variable update integrity v{version}"
            },
            IsPublished = true,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static WorkflowInstanceEntity NewInstance(
        WorkflowDefinitionEntity definition,
        DateTimeOffset now) =>
        new()
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = definition.WorkflowKey,
            Status = WorkflowInstanceStatuses.Running,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static WorkflowJobEntity NewJob(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        DateTimeOffset now) =>
        new()
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            ActivationId = Guid.NewGuid(),
            NodeId = 1,
            NodeName = "Prepare variable updates",
            NodeType = "task",
            Kind = WorkflowJobKinds.InstanceVariableUpdateBatchPrepare,
            QueueClass = WorkflowJobClasses.Activity,
            Phase = InstanceVariableUpdateBatchPhases.Prepare,
            Status = WorkflowJobStatuses.Queued,
            Priority = 0,
            AttemptCount = 0,
            MaxAttempts = 1,
            FailureHandling = WorkflowJobFailureHandling.RetryFirst,
            RetryDelays = [],
            DueAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static async Task AssertSettingAsync(
        string connectionString,
        string expectedValue,
        string expectedDescription)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Value", "Description", count(*) OVER ()
            FROM flowbit.engine_settings
            WHERE "Namespace" = 'WorkflowVariableUpdates'
              AND "Key" = 'MaxBatchInstances'
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedValue, reader.GetString(0));
        Assert.Equal(expectedDescription, reader.GetString(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<string[]> ReadNamesAsync(
        NpgsqlConnection connection,
        string sql)
    {
        var values = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }
        return values.ToArray();
    }

    private static async Task<(string Name, string Definition)[]> ReadDefinitionsAsync(
        NpgsqlConnection connection,
        string sql)
    {
        var values = new List<(string, string)>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add((reader.GetString(0), reader.GetString(1)));
        }
        return values.ToArray();
    }
}
