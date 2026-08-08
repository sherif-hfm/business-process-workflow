using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class AdministrativeActionCandidateRepository(AppDbContext dbContext)
    : IAdministrativeActionCandidateRepository
{
#pragma warning disable EF1002 // SQL fragments are static; all caller values are parameters.
    public async Task<PagedResult<AdministrativeActionCandidateRecord>> SearchAsync(
        AdministrativeActionCandidateQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var (where, arguments) = BuildWhere(query, []);

        var total = await dbContext.Database.SqlQueryRaw<long>(
                $"""
                 SELECT COUNT(*) AS "Value"
                 {CandidateFromSql}
                 {where}
                 """,
                BuildParameters(arguments))
            .SingleAsync(cancellationToken);

        arguments.Add(("skip", (page - 1) * pageSize));
        arguments.Add(("take", pageSize));
        var serializedKeys = await dbContext.Database.SqlQueryRaw<string>(
                $"""
                 SELECT position."PositionKind" || ':' || position."PositionId"::text AS "Value"
                 {CandidateFromSql}
                 {where}
                 ORDER BY position."PositionUpdatedAt" DESC,
                          position."PositionKind",
                          position."PositionId" DESC
                 OFFSET @skip LIMIT @take
                 """,
                BuildParameters(arguments))
            .ToListAsync(cancellationToken);
        var keys = serializedKeys.Select(ParseKey).ToArray();
        var rows = await LoadAsync(keys, query, query.IncludeVariables, cancellationToken);
        return new PagedResult<AdministrativeActionCandidateRecord>(
            rows,
            page,
            pageSize,
            total);
    }

    public async Task<IReadOnlyList<AdministrativeActionCandidateRecord>> MaterializeAsync(
        AdministrativeActionCandidateQuery query,
        IReadOnlyCollection<AdministrativeActionPositionKey> excludedPositions,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var excluded = NormalizeKeys(excludedPositions)
            .Concat(NormalizeKeys(query.ExcludedPositions ?? []))
            .ToHashSet();

        if (query.Positions is { Count: > 0 })
        {
            // Keep explicitly selected positions even if a user action or timer
            // race made them stale after the search page rendered. Preparation
            // will retain the frozen row and classify it as ineligible.
            var selected = NormalizeKeys(query.Positions)
                .Where(position => !excluded.Contains(position))
                .Take(limit + 1)
                .ToArray();
            return await LoadAsync(selected, query, includeVariables: false, cancellationToken);
        }

        var (where, arguments) = BuildWhere(query, excluded);
        arguments.Add(("take", limit + 1));
        var serializedKeys = await dbContext.Database.SqlQueryRaw<string>(
                $"""
                 SELECT position."PositionKind" || ':' || position."PositionId"::text AS "Value"
                 {CandidateFromSql}
                 {where}
                 ORDER BY position."PositionUpdatedAt" DESC,
                          position."PositionKind",
                          position."PositionId" DESC
                 LIMIT @take
                 """,
                BuildParameters(arguments))
            .ToListAsync(cancellationToken);
        return await LoadAsync(
            serializedKeys.Select(ParseKey).ToArray(),
            query,
            includeVariables: false,
            cancellationToken);
    }
#pragma warning restore EF1002

    private const string CandidateFromSql = """
        FROM
        (
            SELECT
                'userTask'::text AS "PositionKind",
                task."Id" AS "PositionId",
                task."InstanceId" AS "InstanceId",
                task."NodeId" AS "NodeId",
                task."UpdatedAt" AS "PositionUpdatedAt"
            FROM flowbit.user_tasks AS task
            INNER JOIN flowbit.execution_tokens AS token
                ON token."Id" = task."TokenId"
               AND token."InstanceId" = task."InstanceId"
               AND token."NodeId" = task."NodeId"
            WHERE task."MultiInstanceExecutionId" IS NULL
              AND task."Status" = 'active'
              AND token."Status" = 'active'

            UNION ALL

            SELECT
                'multiInstanceExecution'::text AS "PositionKind",
                execution."Id" AS "PositionId",
                execution."InstanceId" AS "InstanceId",
                execution."NodeId" AS "NodeId",
                execution."UpdatedAt" AS "PositionUpdatedAt"
            FROM flowbit.multi_instance_executions AS execution
            INNER JOIN flowbit.execution_tokens AS token
                ON token."Id" = execution."TokenId"
               AND token."InstanceId" = execution."InstanceId"
               AND token."NodeId" = execution."NodeId"
            WHERE execution."Status" = 'active'
              AND token."Status" = 'active'
        ) AS position
        INNER JOIN flowbit.workflow_instances AS workflow
            ON workflow."Id" = position."InstanceId"
        """;

    private static (StringBuilder Where, List<(string Name, object Value)> Arguments)
        BuildWhere(
            AdministrativeActionCandidateQuery query,
            IReadOnlyCollection<AdministrativeActionPositionKey> excludedPositions)
    {
        var where = new StringBuilder("""
            WHERE workflow."Status" = 'running'
              AND workflow."WorkflowDefinitionId" = @workflowDefinitionId
              AND position."NodeId" = @sourceNodeId
            """);
        var arguments = new List<(string Name, object Value)>
        {
            ("workflowDefinitionId", query.WorkflowDefinitionId),
            ("sourceNodeId", query.SourceNodeId)
        };

        if (query.InstanceId is long instanceId)
        {
            arguments.Add(("instanceId", instanceId));
            where.Append(" AND workflow.\"Id\" = @instanceId");
        }
        if (!string.IsNullOrWhiteSpace(query.BusinessKey))
        {
            arguments.Add(("businessKey", query.BusinessKey.Trim()));
            where.Append(" AND workflow.\"BusinessKey\" = @businessKey");
        }
        if (query.PositionKind is not null)
        {
            arguments.Add(("positionKind", query.PositionKind));
            where.Append(" AND position.\"PositionKind\" = @positionKind");
        }
        if (query.PositionId is long positionId)
        {
            arguments.Add(("positionId", positionId));
            where.Append(" AND position.\"PositionId\" = @positionId");
        }
        if (query.Positions is { Count: > 0 })
        {
            AppendKeyPredicate(
                where,
                arguments,
                NormalizeKeys(query.Positions),
                excluded: false,
                parameterPrefix: "selected");
        }
        var allExcluded = NormalizeKeys(excludedPositions)
            .Concat(NormalizeKeys(query.ExcludedPositions ?? []))
            .Distinct()
            .ToArray();
        if (allExcluded.Length > 0)
        {
            AppendKeyPredicate(
                where,
                arguments,
                allExcluded,
                excluded: true,
                parameterPrefix: "excluded");
        }
        VariableFilterSqlCompiler.Append(
            where,
            arguments,
            query.VariableFilter,
            "workflow.\"Id\"");
        return (where, arguments);
    }

    private static void AppendKeyPredicate(
        StringBuilder where,
        List<(string Name, object Value)> arguments,
        IReadOnlyCollection<AdministrativeActionPositionKey> positions,
        bool excluded,
        string parameterPrefix)
    {
        var userTaskIds = positions
            .Where(position => position.PositionKind == AdministrativeActionPositionKinds.UserTask)
            .Select(position => position.PositionId)
            .ToArray();
        var executionIds = positions
            .Where(position => position.PositionKind
                == AdministrativeActionPositionKinds.MultiInstanceExecution)
            .Select(position => position.PositionId)
            .ToArray();
        var clauses = new List<string>(2);
        if (userTaskIds.Length > 0)
        {
            var parameterName = parameterPrefix + "UserTaskIds";
            arguments.Add((parameterName, userTaskIds));
            clauses.Add(
                $"(position.\"PositionKind\" = 'userTask' AND position.\"PositionId\" = ANY(@{parameterName}))");
        }
        if (executionIds.Length > 0)
        {
            var parameterName = parameterPrefix + "ExecutionIds";
            arguments.Add((parameterName, executionIds));
            clauses.Add(
                $"(position.\"PositionKind\" = 'multiInstanceExecution' AND position.\"PositionId\" = ANY(@{parameterName}))");
        }

        if (clauses.Count == 0)
        {
            where.Append(excluded ? " AND TRUE" : " AND FALSE");
            return;
        }
        where.Append(excluded ? " AND NOT (" : " AND (");
        where.AppendJoin(" OR ", clauses);
        where.Append(')');
    }

    private async Task<IReadOnlyList<AdministrativeActionCandidateRecord>> LoadAsync(
        IReadOnlyList<AdministrativeActionPositionKey> keys,
        AdministrativeActionCandidateQuery query,
        bool includeVariables,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var userTaskIds = keys
            .Where(key => key.PositionKind == AdministrativeActionPositionKinds.UserTask)
            .Select(key => key.PositionId)
            .Distinct()
            .ToArray();
        var executionIds = keys
            .Where(key => key.PositionKind
                == AdministrativeActionPositionKinds.MultiInstanceExecution)
            .Select(key => key.PositionId)
            .Distinct()
            .ToArray();
        var tasks = await dbContext.UserTasks.AsNoTracking()
            .Where(task => userTaskIds.Contains(task.Id))
            .ToDictionaryAsync(task => task.Id, cancellationToken);
        var executions = await dbContext.MultiInstanceExecutions.AsNoTracking()
            .Where(execution => executionIds.Contains(execution.Id))
            .ToDictionaryAsync(execution => execution.Id, cancellationToken);

        var instanceIds = tasks.Values.Select(task => task.InstanceId)
            .Concat(executions.Values.Select(execution => execution.InstanceId))
            .Distinct()
            .ToArray();
        var tokenIds = tasks.Values.Select(task => task.TokenId)
            .Concat(executions.Values.Select(execution => execution.TokenId))
            .Distinct()
            .ToArray();
        var instances = await dbContext.WorkflowInstances.AsNoTracking()
            .Where(instance => instanceIds.Contains(instance.Id))
            .ToDictionaryAsync(instance => instance.Id, cancellationToken);
        var tokens = await dbContext.ExecutionTokens.AsNoTracking()
            .Where(token => tokenIds.Contains(token.Id))
            .ToDictionaryAsync(token => token.Id, cancellationToken);

        var openCounts = executionIds.Length == 0
            ? new Dictionary<long, int>()
            : await dbContext.UserTasks.AsNoTracking()
                .Where(task => task.MultiInstanceExecutionId != null
                    && executionIds.Contains(task.MultiInstanceExecutionId.Value)
                    && (task.Status == UserTaskStatuses.Active
                        || task.Status == UserTaskStatuses.Pending))
                .GroupBy(task => task.MultiInstanceExecutionId!.Value)
                .Select(group => new { Id = group.Key, Count = group.Count() })
                .ToDictionaryAsync(row => row.Id, row => row.Count, cancellationToken);

        IReadOnlyDictionary<long, IReadOnlyDictionary<string, JsonElement>> variablesByInstance =
            new Dictionary<long, IReadOnlyDictionary<string, JsonElement>>();
        if (includeVariables && instanceIds.Length > 0)
        {
            var values = await dbContext.InstanceVariableCurrentValues.AsNoTracking()
                .Where(value => instanceIds.Contains(value.InstanceId))
                .OrderBy(value => value.InstanceId)
                .ThenBy(value => value.VariableName)
                .ToListAsync(cancellationToken);
            variablesByInstance = values
                .GroupBy(value => value.InstanceId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<string, JsonElement>)group.ToDictionary(
                        value => value.VariableName,
                        value => value.ValueJson.RootElement.Clone(),
                        StringComparer.OrdinalIgnoreCase));
        }

        var subscriptions = tokenIds.Length == 0
            ? new List<TimerSubscriptionEntity>()
            : await dbContext.TimerSubscriptions.AsNoTracking()
                .Where(subscription => subscription.TokenId != null
                    && tokenIds.Contains(subscription.TokenId.Value)
                    && subscription.AttachedToNodeId == query.SourceNodeId)
                .OrderBy(subscription => subscription.TimerNodeId)
                .ThenBy(subscription => subscription.Id)
                .ToListAsync(cancellationToken);
        var subscriptionIds = subscriptions.Select(subscription => subscription.Id).ToArray();
        var latestJobIds = subscriptionIds.Length == 0
            ? Array.Empty<long>()
            : await dbContext.WorkflowJobs.AsNoTracking()
                .Where(job => job.TimerSubscriptionId != null
                    && subscriptionIds.Contains(job.TimerSubscriptionId.Value))
                .GroupBy(job => job.TimerSubscriptionId!.Value)
                .Select(group => group.Max(job => job.Id))
                .ToArrayAsync(cancellationToken);
        var latestJobs = latestJobIds.Length == 0
            ? new Dictionary<long, WorkflowJobEntity>()
            : (await dbContext.WorkflowJobs.AsNoTracking()
                .Where(job => latestJobIds.Contains(job.Id))
                .ToListAsync(cancellationToken))
                .Where(job => job.TimerSubscriptionId is not null)
                .ToDictionary(job => job.TimerSubscriptionId!.Value);

        var result = new List<AdministrativeActionCandidateRecord>(keys.Count);
        foreach (var key in keys)
        {
            long instanceId;
            long tokenId;
            int nodeId;
            string nodeName;
            string? nodeExternalId;
            DateTimeOffset positionUpdatedAt;
            long? userTaskId;
            long? multiInstanceExecutionId;
            int affectedTaskCount;
            if (key.PositionKind == AdministrativeActionPositionKinds.UserTask
                && tasks.TryGetValue(key.PositionId, out var task))
            {
                instanceId = task.InstanceId;
                tokenId = task.TokenId;
                nodeId = task.NodeId;
                nodeName = task.NodeName;
                nodeExternalId = task.NodeExternalId;
                positionUpdatedAt = task.UpdatedAt;
                userTaskId = task.Id;
                multiInstanceExecutionId = null;
                affectedTaskCount = 1;
            }
            else if (key.PositionKind
                    == AdministrativeActionPositionKinds.MultiInstanceExecution
                && executions.TryGetValue(key.PositionId, out var execution)
                && tokens.TryGetValue(execution.TokenId, out var executionToken))
            {
                instanceId = execution.InstanceId;
                tokenId = execution.TokenId;
                nodeId = execution.NodeId;
                nodeName = executionToken.NodeName;
                nodeExternalId = executionToken.NodeExternalId;
                positionUpdatedAt = execution.UpdatedAt;
                userTaskId = null;
                multiInstanceExecutionId = execution.Id;
                affectedTaskCount = openCounts.GetValueOrDefault(execution.Id);
            }
            else
            {
                continue;
            }

            if (!instances.TryGetValue(instanceId, out var instance)
                || !tokens.TryGetValue(tokenId, out var token)
                || instance.WorkflowDefinitionId != query.WorkflowDefinitionId
                || nodeId != query.SourceNodeId
                || token.InstanceId != instanceId)
            {
                continue;
            }

            var timerBoundaries = subscriptions
                .Where(subscription => subscription.TokenId == tokenId
                    && subscription.ActivationId == token.ActivationId)
                .Select(subscription =>
                {
                    latestJobs.TryGetValue(subscription.Id, out var latestJob);
                    return new AdministrativeTimerBoundaryStateRecord(
                        subscription.TimerNodeId,
                        subscription.Id,
                        latestJob?.Id,
                        subscription.Status,
                        subscription.NextDueAt,
                        subscription.Occurrence,
                        subscription.UpdatedAt);
                })
                .ToArray();
            variablesByInstance.TryGetValue(instanceId, out var variables);
            result.Add(new AdministrativeActionCandidateRecord(
                key.PositionKind,
                key.PositionId,
                userTaskId,
                multiInstanceExecutionId,
                instanceId,
                tokenId,
                token.ActivationId,
                instance.WorkflowDefinitionId,
                instance.WorkflowKey,
                instance.BusinessKey,
                nodeId,
                nodeName,
                nodeExternalId,
                positionUpdatedAt,
                affectedTaskCount,
                timerBoundaries,
                variables));
        }
        return result;
    }

    private static void ValidateQuery(AdministrativeActionCandidateQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.WorkflowDefinitionId <= 0 || query.SourceNodeId <= 0)
        {
            throw new ArgumentException(
                "Workflow definition and source node identifiers must be positive.",
                nameof(query));
        }
        if (query.PositionKind is not null
            && !AdministrativeActionPositionKinds.IsKnown(query.PositionKind))
        {
            throw new ArgumentException("The position kind is invalid.", nameof(query));
        }
        if ((query.PositionId is null) != (query.PositionKind is null))
        {
            throw new ArgumentException(
                "Position kind and position id must be supplied together.",
                nameof(query));
        }
        if (query.PositionId is <= 0)
        {
            throw new ArgumentException("The position id must be positive.", nameof(query));
        }
        _ = NormalizeKeys(query.Positions ?? []);
        _ = NormalizeKeys(query.ExcludedPositions ?? []);
    }

    private static IReadOnlyList<AdministrativeActionPositionKey> NormalizeKeys(
        IEnumerable<AdministrativeActionPositionKey> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var normalized = new List<AdministrativeActionPositionKey>();
        var seen = new HashSet<AdministrativeActionPositionKey>();
        foreach (var position in positions)
        {
            if (!AdministrativeActionPositionKinds.IsKnown(position.PositionKind)
                || position.PositionId <= 0)
            {
                throw new ArgumentException("A selected position identity is invalid.");
            }
            if (seen.Add(position))
            {
                normalized.Add(position);
            }
        }
        return normalized;
    }

    private static AdministrativeActionPositionKey ParseKey(string serialized)
    {
        var separator = serialized.IndexOf(':');
        if (separator <= 0
            || !long.TryParse(serialized[(separator + 1)..], out var id))
        {
            throw new InvalidOperationException(
                "The administrative candidate query returned an invalid position identity.");
        }
        return new AdministrativeActionPositionKey(serialized[..separator], id);
    }

    private static NpgsqlParameter[] BuildParameters(
        IEnumerable<(string Name, object Value)> arguments) =>
        arguments.Select(argument => new NpgsqlParameter(argument.Name, argument.Value)).ToArray();
}
