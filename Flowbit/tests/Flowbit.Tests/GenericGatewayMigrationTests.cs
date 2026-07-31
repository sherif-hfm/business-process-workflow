using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class GenericGatewayMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20260728154834_AddUserDelegations";

    [Fact]
    public async Task MigrationTranslatesEveryLegacyParallelCompletionReason()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await SeedLegacyCompletionReasonsAsync(connectionString);

            await MigrateAsync(connectionString);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT "CompletionReason"
                FROM flowbit.node_executions
                ORDER BY "NodeId"
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync();

            var completionReasons = new List<string>();
            while (await reader.ReadAsync())
            {
                completionReasons.Add(reader.GetString(0));
            }

            Assert.Equal(
                [
                    "gatewayScopeCancelled",
                    "gatewayJoinMerged",
                    "scopedInterrupt",
                    "scopedInterruptSkipped"
                ],
                completionReasons);
        });
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "generic_gateway_" + Guid.NewGuid().ToString("N");
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

    private static async Task SeedLegacyCompletionReasonsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var definition = new NpgsqlCommand(
            """
            INSERT INTO flowbit.workflow_definitions
                ("Name", "WorkflowKey", "Version", "Definition", "IsPublished", "IsDefault", "CreatedAt")
            VALUES
                ('Legacy gateway completion reasons', 'legacy-gateway-reasons', 1,
                 @definition, true, true, now())
            RETURNING "Id"
            """,
            connection,
            transaction);
        definition.Parameters.AddWithValue(
            "definition",
            NpgsqlDbType.Jsonb,
            """
            {"id":"legacy-gateway-reasons","name":"Legacy gateway completion reasons","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}
            """);
        var definitionId = (long)(await definition.ExecuteScalarAsync())!;

        await using var instance = new NpgsqlCommand(
            """
            INSERT INTO flowbit.workflow_instances
                ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy", "CreatedAt", "UpdatedAt")
            VALUES
                (@definitionId, 'legacy-gateway-reasons', 'running', 'migration-test', now(), now())
            RETURNING "Id"
            """,
            connection,
            transaction);
        instance.Parameters.AddWithValue("definitionId", definitionId);
        var instanceId = (long)(await instance.ExecuteScalarAsync())!;

        await using var token = new NpgsqlCommand(
            """
            INSERT INTO flowbit.execution_tokens
                ("InstanceId", "NodeId", "NodeName", "NodeType", "Status", "CreatedAt", "UpdatedAt")
            VALUES
                (@instanceId, 99, 'Waiting task', 'userTask', 'active', now(), now())
            RETURNING "Id"
            """,
            connection,
            transaction);
        token.Parameters.AddWithValue("instanceId", instanceId);
        var tokenId = (long)(await token.ExecuteScalarAsync())!;

        await using var executions = new NpgsqlCommand(
            """
            INSERT INTO flowbit.node_executions
                ("InstanceId", "ExecutionTokenId", "NodeId", "NodeName", "NodeType",
                 "ExecutionKind", "Status", "CompletionReason",
                 "CreatedAt", "StartedAt", "UpdatedAt", "CompletedAt", "IsCutoverSeeded")
            VALUES
                (@instanceId, @tokenId, 1, 'Cancelled parallel scope', 'task',
                 'node', 'cancelled', 'parallelScopeCancelled', now(), now(), now(), now(), false),
                (@instanceId, @tokenId, 2, 'Merged parallel join', 'parallelGateway',
                 'node', 'merged', 'parallelJoinMerged', now(), now(), now(), now(), false),
                (@instanceId, @tokenId, 3, 'Parallel interrupt', 'scopedInterruptEvent',
                 'node', 'completed', 'parallelInterrupt', now(), now(), now(), now(), false),
                (@instanceId, @tokenId, 4, 'Skipped parallel interrupt', 'scopedInterruptEvent',
                 'node', 'completed', 'parallelInterruptSkipped', now(), now(), now(), now(), false)
            """,
            connection,
            transaction);
        executions.Parameters.AddWithValue("instanceId", instanceId);
        executions.Parameters.AddWithValue("tokenId", tokenId);
        await executions.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }
}
