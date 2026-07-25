using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class NodeExecutionMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20260724125626_AddParallelGatewayScopes";
    private const string TargetMigration = "20260725111017_AddNodeExecutions";
    private const int LegacyHistoryNodeId = 10;

    [Fact]
    public async Task MigrationSeedsOnlyOpenWorkAtCutover()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var legacy = await SeedLegacyStateAsync(connectionString);
            var beforeCutover = await ReadDatabaseTimeAsync(connectionString);

            await MigrateAsync(connectionString, TargetMigration);

            var afterCutover = await ReadDatabaseTimeAsync(connectionString);
            var executions = await ReadNodeExecutionsAsync(connectionString);

            Assert.Equal(6, executions.Count);
            Assert.All(executions, execution =>
            {
                Assert.True(execution.IsCutoverSeeded);
                Assert.Null(execution.CompletionReason);
                Assert.Null(execution.CompletedAt);
                Assert.InRange(execution.CreatedAt, beforeCutover, afterCutover);
                Assert.Equal(execution.CreatedAt, execution.UpdatedAt);
            });

            var normalTask = Assert.Single(
                executions,
                execution => execution.UserTaskId == legacy.NormalTaskId);
            Assert.Equal(legacy.NormalInstanceId, normalTask.InstanceId);
            Assert.Equal(legacy.NormalTokenId, normalTask.ExecutionTokenId);
            Assert.Equal("node", normalTask.ExecutionKind);
            Assert.Equal("active", normalTask.Status);
            Assert.Equal(normalTask.CreatedAt, normalTask.StartedAt);
            Assert.Null(normalTask.MultiInstanceExecutionId);
            Assert.Null(normalTask.ItemIndex);

            var activeMultiInstanceItem = Assert.Single(
                executions,
                execution => execution.UserTaskId == legacy.ActiveMultiInstanceTaskId);
            Assert.Equal(legacy.MultiInstanceId, activeMultiInstanceItem.InstanceId);
            Assert.Equal(legacy.MultiInstanceTokenId, activeMultiInstanceItem.ExecutionTokenId);
            Assert.Equal(legacy.MultiInstanceExecutionId, activeMultiInstanceItem.MultiInstanceExecutionId);
            Assert.Equal(0, activeMultiInstanceItem.ItemIndex);
            Assert.Equal("userTaskItem", activeMultiInstanceItem.ExecutionKind);
            Assert.Equal("active", activeMultiInstanceItem.Status);
            Assert.Equal(activeMultiInstanceItem.CreatedAt, activeMultiInstanceItem.StartedAt);

            var pendingMultiInstanceItem = Assert.Single(
                executions,
                execution => execution.UserTaskId == legacy.PendingMultiInstanceTaskId);
            Assert.Equal(legacy.MultiInstanceExecutionId, pendingMultiInstanceItem.MultiInstanceExecutionId);
            Assert.Equal(1, pendingMultiInstanceItem.ItemIndex);
            Assert.Equal("userTaskItem", pendingMultiInstanceItem.ExecutionKind);
            Assert.Equal("pending", pendingMultiInstanceItem.Status);
            Assert.Null(pendingMultiInstanceItem.StartedAt);

            var waitingToken = Assert.Single(
                executions,
                execution => execution.ExecutionTokenId == legacy.WaitingTokenId);
            Assert.Equal(legacy.WaitingInstanceId, waitingToken.InstanceId);
            Assert.Null(waitingToken.UserTaskId);
            Assert.Equal("node", waitingToken.ExecutionKind);
            Assert.Equal("active", waitingToken.Status);
            Assert.Equal(waitingToken.CreatedAt, waitingToken.StartedAt);
            Assert.Equal("""["message-reader"]""", waitingToken.NodeRolesJson);

            var ambiguousRows = executions
                .Where(execution => execution.ExecutionTokenId == legacy.AmbiguousTokenId)
                .ToArray();
            Assert.Equal(2, ambiguousRows.Length);
            Assert.All(ambiguousRows, execution =>
            {
                Assert.Equal("node", execution.ExecutionKind);
                Assert.Equal("active", execution.Status);
            });
            Assert.Contains(
                ambiguousRows,
                execution => execution.UserTaskId == legacy.FirstAmbiguousTaskId);
            Assert.Contains(
                ambiguousRows,
                execution => execution.UserTaskId == legacy.SecondAmbiguousTaskId);

            Assert.DoesNotContain(
                executions,
                execution => execution.UserTaskId == legacy.CompletedMultiInstanceTaskId);
            Assert.DoesNotContain(
                executions,
                execution => execution.InstanceId == legacy.CompletedInstanceId);
            Assert.DoesNotContain(
                executions,
                execution => execution.NodeId == LegacyHistoryNodeId);
            Assert.DoesNotContain(
                executions,
                execution => execution.ExecutionTokenId == legacy.MultiInstanceTokenId
                    && execution.ExecutionKind == "node");

            var pointers = await ReadTokenPointersAsync(connectionString);
            Assert.Equal(normalTask.Id, pointers[legacy.NormalTokenId]);
            Assert.Equal(waitingToken.Id, pointers[legacy.WaitingTokenId]);
            Assert.Null(pointers[legacy.MultiInstanceTokenId]);
            Assert.Null(pointers[legacy.AmbiguousTokenId]);
            Assert.Null(pointers[legacy.CompletedTokenId]);
            Assert.Equal(2, pointers.Count(pointer => pointer.Value is not null));

            Assert.Equal(0, await CountInvalidPointersAsync(connectionString));
        });
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "node_execution_cutover_" + Guid.NewGuid().ToString("N");
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

    private static async Task<LegacyState> SeedLegacyStateAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH definition_row AS (
                INSERT INTO flowbit.workflow_definitions
                    ("Name", "WorkflowKey", "Version", "Definition",
                     "IsPublished", "IsDefault", "CreatedAt")
                VALUES
                    ('Node execution cutover migration', 'node-execution-cutover', 1, @definition,
                     true, true, TIMESTAMPTZ '2025-01-01 00:00:00+00')
                RETURNING "Id"
            ),
            normal_instance AS (
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 'node-execution-cutover', 'running', 'legacy-starter',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM definition_row
                RETURNING "Id"
            ),
            normal_token AS (
                INSERT INTO flowbit.execution_tokens
                    ("InstanceId", "NodeId", "NodeName", "NodeExternalId", "NodeType",
                     "ArrivedViaFlowId", "Status", "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 20, 'Legacy approval', 'legacy-approval', 'userTask',
                    120, 'active',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM normal_instance
                RETURNING "Id", "InstanceId"
            ),
            normal_task AS (
                INSERT INTO flowbit.user_tasks
                    ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId",
                     "Roles", "RequiresClaim", "RequiresAssignment", "Status",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "InstanceId", "Id", 20, 'Legacy approval', 'legacy-approval',
                    ARRAY['manager']::text[], false, false, 'active',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM normal_token
                RETURNING "Id"
            ),
            normal_history AS (
                INSERT INTO flowbit.instance_history
                    ("InstanceId", "FromStepId", "ToStepId", "PerformedBy", "Note", "PerformedAt")
                SELECT
                    "Id", 10, 20, 'legacy-user', 'legacy-completed-visit',
                    TIMESTAMPTZ '2025-01-01 12:00:00+00'
                FROM normal_instance
                RETURNING "Id"
            ),
            mi_instance AS (
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 'node-execution-cutover', 'running', 'legacy-starter',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM definition_row
                RETURNING "Id"
            ),
            mi_token AS (
                INSERT INTO flowbit.execution_tokens
                    ("InstanceId", "NodeId", "NodeName", "NodeExternalId", "NodeType",
                     "ArrivedViaFlowId", "Status", "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 30, 'Legacy multi approval', 'legacy-multi-approval', 'userTask',
                    130, 'active',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM mi_instance
                RETURNING "Id", "InstanceId"
            ),
            mi_execution AS (
                INSERT INTO flowbit.multi_instance_executions
                    ("InstanceId", "TokenId", "NodeId", "Mode", "Source", "OnePerActor",
                     "ResultVariable", "Status", "TotalCount", "CompletedCount", "CancelledCount",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "InstanceId", "Id", 30, 'sequential', 'collection', false,
                    'approvals', 'active', 3, 1, 0,
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM mi_token
                RETURNING "Id", "InstanceId", "TokenId"
            ),
            mi_active_task AS (
                INSERT INTO flowbit.user_tasks
                    ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId",
                     "Roles", "RequiresClaim", "RequiresAssignment", "Status",
                     "MultiInstanceExecutionId", "ItemIndex", "ItemValueJson", "Assignee",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "InstanceId", "TokenId", 30, 'Legacy multi approval', 'legacy-multi-approval',
                    ARRAY['manager']::text[], false, false, 'active',
                    "Id", 0, '"alice"'::jsonb, 'alice',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM mi_execution
                RETURNING "Id"
            ),
            mi_pending_task AS (
                INSERT INTO flowbit.user_tasks
                    ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId",
                     "Roles", "RequiresClaim", "RequiresAssignment", "Status",
                     "MultiInstanceExecutionId", "ItemIndex", "ItemValueJson", "Assignee",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "InstanceId", "TokenId", 30, 'Legacy multi approval', 'legacy-multi-approval',
                    ARRAY['manager']::text[], false, false, 'pending',
                    "Id", 1, '"bob"'::jsonb, 'bob',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM mi_execution
                RETURNING "Id"
            ),
            mi_completed_task AS (
                INSERT INTO flowbit.user_tasks
                    ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId",
                     "Roles", "RequiresClaim", "RequiresAssignment", "Status",
                     "MultiInstanceExecutionId", "ItemIndex", "ItemValueJson", "Assignee",
                     "SelectedFlowId", "CompletedBy", "CreatedAt", "UpdatedAt", "CompletedAt")
                SELECT
                    "InstanceId", "TokenId", 30, 'Legacy multi approval', 'legacy-multi-approval',
                    ARRAY['manager']::text[], false, false, 'completed',
                    "Id", 2, '"carol"'::jsonb, 'carol',
                    230, 'carol',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM mi_execution
                RETURNING "Id"
            ),
            waiting_instance AS (
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 'node-execution-cutover', 'running', 'legacy-starter',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM definition_row
                RETURNING "Id"
            ),
            waiting_token AS (
                INSERT INTO flowbit.execution_tokens
                    ("InstanceId", "NodeId", "NodeName", "NodeExternalId", "NodeType",
                     "ArrivedViaFlowId", "Status", "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 40, 'Legacy message wait', 'legacy-message-wait',
                    'intermediateMessageCatchEvent', 140, 'active',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM waiting_instance
                RETURNING "Id", "InstanceId"
            ),
            ambiguous_instance AS (
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 'node-execution-cutover', 'running', 'legacy-starter',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM definition_row
                RETURNING "Id"
            ),
            ambiguous_token AS (
                INSERT INTO flowbit.execution_tokens
                    ("InstanceId", "NodeId", "NodeName", "NodeExternalId", "NodeType",
                     "ArrivedViaFlowId", "Status", "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 50, 'Legacy ambiguous work', 'legacy-ambiguous-work', 'userTask',
                    150, 'active',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM ambiguous_instance
                RETURNING "Id", "InstanceId"
            ),
            ambiguous_task_one AS (
                INSERT INTO flowbit.user_tasks
                    ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId",
                     "Roles", "RequiresClaim", "RequiresAssignment", "Status",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "InstanceId", "Id", 50, 'Legacy ambiguous work', 'legacy-ambiguous-work',
                    ARRAY['manager']::text[], false, false, 'active',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM ambiguous_token
                RETURNING "Id"
            ),
            ambiguous_task_two AS (
                INSERT INTO flowbit.user_tasks
                    ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId",
                     "Roles", "RequiresClaim", "RequiresAssignment", "Status",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "InstanceId", "Id", 50, 'Legacy ambiguous work', 'legacy-ambiguous-work',
                    ARRAY['manager']::text[], false, false, 'active',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM ambiguous_token
                RETURNING "Id"
            ),
            completed_instance AS (
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy",
                     "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 'node-execution-cutover', 'completed', 'legacy-starter',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM definition_row
                RETURNING "Id"
            ),
            completed_token AS (
                INSERT INTO flowbit.execution_tokens
                    ("InstanceId", "NodeId", "NodeName", "NodeExternalId", "NodeType",
                     "ArrivedViaFlowId", "Status", "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", 99, 'Legacy end', 'legacy-end', 'endEvent',
                    199, 'completed',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM completed_instance
                RETURNING "Id", "InstanceId"
            ),
            completed_task AS (
                INSERT INTO flowbit.user_tasks
                    ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId",
                     "Roles", "RequiresClaim", "RequiresAssignment", "Status",
                     "CompletedBy", "CreatedAt", "UpdatedAt", "CompletedAt")
                SELECT
                    "InstanceId", "Id", 98, 'Legacy completed work', 'legacy-completed-work',
                    ARRAY['manager']::text[], false, false, 'completed',
                    'legacy-user',
                    TIMESTAMPTZ '2025-01-01 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM completed_token
                RETURNING "Id"
            ),
            completed_history AS (
                INSERT INTO flowbit.instance_history
                    ("InstanceId", "FromStepId", "ToStepId", "PerformedBy", "Note", "PerformedAt")
                SELECT
                    "InstanceId", 98, 99, 'legacy-user', 'legacy-completed-visit',
                    TIMESTAMPTZ '2025-01-02 00:00:00+00'
                FROM completed_token
                RETURNING "Id"
            )
            SELECT
                normal_instance."Id",
                normal_token."Id",
                normal_task."Id",
                mi_instance."Id",
                mi_token."Id",
                mi_execution."Id",
                mi_active_task."Id",
                mi_pending_task."Id",
                mi_completed_task."Id",
                waiting_instance."Id",
                waiting_token."Id",
                ambiguous_token."Id",
                ambiguous_task_one."Id",
                ambiguous_task_two."Id",
                completed_instance."Id",
                completed_token."Id"
            FROM normal_instance
            CROSS JOIN normal_token
            CROSS JOIN normal_task
            CROSS JOIN normal_history
            CROSS JOIN mi_instance
            CROSS JOIN mi_token
            CROSS JOIN mi_execution
            CROSS JOIN mi_active_task
            CROSS JOIN mi_pending_task
            CROSS JOIN mi_completed_task
            CROSS JOIN waiting_instance
            CROSS JOIN waiting_token
            CROSS JOIN ambiguous_instance
            CROSS JOIN ambiguous_token
            CROSS JOIN ambiguous_task_one
            CROSS JOIN ambiguous_task_two
            CROSS JOIN completed_instance
            CROSS JOIN completed_token
            CROSS JOIN completed_task
            CROSS JOIN completed_history
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "definition",
            NpgsqlDbType.Jsonb,
            """
            {
              "id": "node-execution-cutover",
              "name": "Node execution cutover migration",
              "flowNodes": [
                {
                  "id": 40,
                  "name": "Legacy message wait",
                  "type": "intermediateMessageCatchEvent",
                  "roles": ["message-reader"]
                },
                {
                  "id": 50,
                  "name": "Legacy ambiguous work",
                  "type": "userTask",
                  "roles": ["ambiguous-reader"]
                }
              ],
              "sequenceFlows": [],
              "variables": [],
              "lanes": []
            }
            """);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var state = new LegacyState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetInt64(15));
        Assert.False(await reader.ReadAsync());
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return state;
    }

    private static async Task<DateTime> ReadDatabaseTimeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<NodeExecutionSeedRow>> ReadNodeExecutionsAsync(string connectionString)
    {
        var rows = new List<NodeExecutionSeedRow>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                "Id", "InstanceId", "ExecutionTokenId", "UserTaskId",
                "MultiInstanceExecutionId", "ItemIndex", "NodeId",
                "ExecutionKind", "NodeRolesJson"::text, "Status", "CompletionReason",
                "CreatedAt", "StartedAt", "UpdatedAt", "CompletedAt",
                "IsCutoverSeeded"
            FROM flowbit.node_executions
            ORDER BY "Id"
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new NodeExecutionSeedRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetDateTime(11),
                reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                reader.GetDateTime(13),
                reader.IsDBNull(14) ? null : reader.GetDateTime(14),
                reader.GetBoolean(15)));
        }

        return rows;
    }

    private static async Task<Dictionary<long, long?>> ReadTokenPointersAsync(string connectionString)
    {
        var pointers = new Dictionary<long, long?>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Id", "CurrentNodeExecutionId"
            FROM flowbit.execution_tokens
            ORDER BY "Id"
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            pointers.Add(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1));
        }

        return pointers;
    }

    private static async Task<long> CountInvalidPointersAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM flowbit.execution_tokens token
            JOIN flowbit.node_executions execution
              ON execution."Id" = token."CurrentNodeExecutionId"
            WHERE execution."ExecutionTokenId" <> token."Id"
               OR execution."ExecutionKind" <> 'node'
               OR execution."Status" <> 'active'
            """,
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record LegacyState(
        long NormalInstanceId,
        long NormalTokenId,
        long NormalTaskId,
        long MultiInstanceId,
        long MultiInstanceTokenId,
        long MultiInstanceExecutionId,
        long ActiveMultiInstanceTaskId,
        long PendingMultiInstanceTaskId,
        long CompletedMultiInstanceTaskId,
        long WaitingInstanceId,
        long WaitingTokenId,
        long AmbiguousTokenId,
        long FirstAmbiguousTaskId,
        long SecondAmbiguousTaskId,
        long CompletedInstanceId,
        long CompletedTokenId);

    private sealed record NodeExecutionSeedRow(
        long Id,
        long InstanceId,
        long ExecutionTokenId,
        long? UserTaskId,
        long? MultiInstanceExecutionId,
        int? ItemIndex,
        int NodeId,
        string ExecutionKind,
        string? NodeRolesJson,
        string Status,
        string? CompletionReason,
        DateTime CreatedAt,
        DateTime? StartedAt,
        DateTime UpdatedAt,
        DateTime? CompletedAt,
        bool IsCutoverSeeded);
}
