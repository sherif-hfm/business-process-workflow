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
        ArgumentNullException.ThrowIfNull(query);
        var targets = NormalizeTargets(query.Targets);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var (where, arguments) = BuildWhere(query, targets, []);

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
        var taskIds = await dbContext.Database.SqlQueryRaw<long>(
                $"""
                 SELECT ut."Id" AS "Value"
                 {CandidateFromSql}
                 {where}
                 ORDER BY w."UpdatedAt" DESC, ut."Id" DESC
                 OFFSET @skip LIMIT @take
                 """,
                BuildParameters(arguments))
            .ToListAsync(cancellationToken);
        var rows = await LoadAsync(taskIds, targets, query.IncludeVariables, cancellationToken);
        return new PagedResult<AdministrativeActionCandidateRecord>(rows, page, pageSize, total);
    }

    public async Task<IReadOnlyList<AdministrativeActionCandidateRecord>> MaterializeAsync(
        AdministrativeActionCandidateQuery query,
        IReadOnlyCollection<long> excludedUserTaskIds,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var targets = NormalizeTargets(query.Targets);
        if (query.UserTaskIds is { Count: > 0 })
        {
            var excluded = excludedUserTaskIds.ToHashSet();
            var selectedIds = query.UserTaskIds
                .Where(id => !excluded.Contains(id))
                .Distinct()
                .ToArray();
            if (selectedIds.Length == 0)
            {
                return [];
            }
            var explicitArguments = new List<(string Name, object Value)>
            {
                ("workflowDefinitionIds", targets.Select(target => target.WorkflowDefinitionId).ToArray()),
                ("flowIds", targets.Select(target => target.FlowId).ToArray()),
                ("sourceNodeIds", targets.Select(target => target.SourceNodeId).ToArray()),
                ("userTaskIds", selectedIds),
                ("take", limit + 1)
            };
            // Explicit selections preserve existing tasks that became stale
            // after the search screen was rendered. Preparation will classify
            // those frozen rows as ineligible instead of silently dropping
            // them. Exact definition/flow targets prevent cross-version or
            // cross-workflow probing.
            var explicitTaskIds = await dbContext.Database.SqlQueryRaw<long>(
                    """
                    SELECT ut."Id" AS "Value"
                    FROM flowbit.user_tasks AS ut
                    INNER JOIN flowbit.workflow_instances AS w
                        ON w."Id" = ut."InstanceId"
                    INNER JOIN unnest(
                        CAST(@workflowDefinitionIds AS bigint[]),
                        CAST(@flowIds AS integer[]),
                        CAST(@sourceNodeIds AS integer[]))
                        AS selected("WorkflowDefinitionId", "FlowId", "SourceNodeId")
                        ON selected."WorkflowDefinitionId" = w."WorkflowDefinitionId"
                       AND selected."SourceNodeId" = ut."NodeId"
                    INNER JOIN flowbit.workflow_definitions AS selected_definition
                        ON selected_definition."Id" = selected."WorkflowDefinitionId"
                    INNER JOIN LATERAL jsonb_array_elements(
                        COALESCE(
                            selected_definition."Definition" -> 'sequenceFlows',
                            '[]'::jsonb)) AS selected_flow(value)
                        ON (selected_flow.value ->> 'id')::integer = selected."FlowId"
                       AND (selected_flow.value ->> 'sourceRef')::integer = selected."SourceNodeId"
                    WHERE ut."Id" = ANY(@userTaskIds)
                    ORDER BY ut."Id"
                    LIMIT @take
                    """,
                    BuildParameters(explicitArguments))
                .ToListAsync(cancellationToken);
            return await LoadAsync(
                explicitTaskIds,
                targets,
                includeVariables: false,
                cancellationToken);
        }

        var (where, arguments) = BuildWhere(query, targets, excludedUserTaskIds);
        arguments.Add(("take", limit + 1));
        var taskIds = await dbContext.Database.SqlQueryRaw<long>(
                $"""
                 SELECT ut."Id" AS "Value"
                 {CandidateFromSql}
                 {where}
                 ORDER BY w."UpdatedAt" DESC, ut."Id" DESC
                 LIMIT @take
                 """,
                BuildParameters(arguments))
            .ToListAsync(cancellationToken);
        return await LoadAsync(taskIds, targets, includeVariables: false, cancellationToken);
    }
#pragma warning restore EF1002

    private const string CandidateFromSql = """
        FROM flowbit.user_tasks AS ut
        INNER JOIN flowbit.workflow_instances AS w ON w."Id" = ut."InstanceId"
        INNER JOIN flowbit.execution_tokens AS token ON token."Id" = ut."TokenId"
        INNER JOIN unnest(
            CAST(@workflowDefinitionIds AS bigint[]),
            CAST(@flowIds AS integer[]),
            CAST(@sourceNodeIds AS integer[]))
            AS selected("WorkflowDefinitionId", "FlowId", "SourceNodeId")
            ON selected."WorkflowDefinitionId" = w."WorkflowDefinitionId"
           AND selected."SourceNodeId" = ut."NodeId"
        INNER JOIN flowbit.workflow_definitions AS selected_definition
            ON selected_definition."Id" = selected."WorkflowDefinitionId"
        INNER JOIN LATERAL jsonb_array_elements(
            COALESCE(
                selected_definition."Definition" -> 'sequenceFlows',
                '[]'::jsonb)) AS selected_flow(value)
            ON (selected_flow.value ->> 'id')::integer = selected."FlowId"
           AND (selected_flow.value ->> 'sourceRef')::integer = selected."SourceNodeId"
        """;

    private static (StringBuilder Where, List<(string Name, object Value)> Arguments)
        BuildWhere(
            AdministrativeActionCandidateQuery query,
            IReadOnlyList<AdministrativeActionFlowTarget> targets,
            IReadOnlyCollection<long> excludedUserTaskIds)
    {
        var where = new StringBuilder("""
            WHERE w."Status" = 'running'
              AND ut."Status" = 'active'
              AND ut."MultiInstanceExecutionId" IS NULL
              AND token."Status" = 'active'
              AND token."InstanceId" = w."Id"
              AND token."NodeId" = ut."NodeId"
              AND (
                    SELECT COUNT(*)
                    FROM flowbit.execution_tokens AS active_token
                    WHERE active_token."InstanceId" = w."Id"
                      AND active_token."Status" = 'active'
                  ) = 1
              AND (
                    SELECT COUNT(*)
                    FROM flowbit.user_tasks AS active_task
                    WHERE active_task."InstanceId" = w."Id"
                      AND active_task."Status" = 'active'
                  ) = 1
            """);
        var arguments = new List<(string Name, object Value)>
        {
            ("workflowDefinitionIds", targets.Select(target => target.WorkflowDefinitionId).ToArray()),
            ("flowIds", targets.Select(target => target.FlowId).ToArray()),
            ("sourceNodeIds", targets.Select(target => target.SourceNodeId).ToArray())
        };

        if (query.UserTaskId is long taskId)
        {
            arguments.Add(("userTaskId", taskId));
            where.Append(" AND ut.\"Id\" = @userTaskId");
        }
        if (query.InstanceId is long instanceId)
        {
            arguments.Add(("instanceId", instanceId));
            where.Append(" AND w.\"Id\" = @instanceId");
        }
        if (!string.IsNullOrWhiteSpace(query.BusinessKey))
        {
            arguments.Add(("businessKey", query.BusinessKey.Trim()));
            where.Append(" AND w.\"BusinessKey\" = @businessKey");
        }
        if (query.UserTaskIds is { Count: > 0 })
        {
            arguments.Add(("userTaskIds", query.UserTaskIds.Distinct().ToArray()));
            where.Append(" AND ut.\"Id\" = ANY(@userTaskIds)");
        }
        if (excludedUserTaskIds.Count > 0)
        {
            arguments.Add(("excludedUserTaskIds", excludedUserTaskIds.Distinct().ToArray()));
            where.Append(" AND NOT (ut.\"Id\" = ANY(@excludedUserTaskIds))");
        }
        VariableFilterSqlCompiler.Append(where, arguments, query.VariableFilter, "w.\"Id\"");
        return (where, arguments);
    }

    private async Task<IReadOnlyList<AdministrativeActionCandidateRecord>> LoadAsync(
        IReadOnlyList<long> taskIds,
        IReadOnlyList<AdministrativeActionFlowTarget> targets,
        bool includeVariables,
        CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return [];
        }

        var tasks = await dbContext.UserTasks.AsNoTracking()
            .Where(task => taskIds.Contains(task.Id))
            .ToDictionaryAsync(task => task.Id, cancellationToken);
        var instanceIds = tasks.Values.Select(task => task.InstanceId).Distinct().ToArray();
        var instances = await dbContext.WorkflowInstances.AsNoTracking()
            .Where(instance => instanceIds.Contains(instance.Id))
            .ToDictionaryAsync(instance => instance.Id, cancellationToken);

        IReadOnlyDictionary<long, IReadOnlyDictionary<string, JsonElement>> variablesByInstance =
            new Dictionary<long, IReadOnlyDictionary<string, JsonElement>>();
        if (includeVariables)
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

        var result = new List<AdministrativeActionCandidateRecord>(taskIds.Count);
        var targetsByDefinition = targets.ToDictionary(
            target => target.WorkflowDefinitionId);
        foreach (var taskId in taskIds)
        {
            if (!tasks.TryGetValue(taskId, out var task)
                || !instances.TryGetValue(task.InstanceId, out var instance)
                || !targetsByDefinition.TryGetValue(
                    instance.WorkflowDefinitionId,
                    out var target))
            {
                continue;
            }
            variablesByInstance.TryGetValue(instance.Id, out var variables);
            result.Add(new AdministrativeActionCandidateRecord(
                task.Id,
                instance.Id,
                task.TokenId,
                instance.WorkflowDefinitionId,
                target.FlowId,
                instance.WorkflowKey,
                instance.BusinessKey,
                task.NodeId,
                task.NodeName,
                task.NodeExternalId,
                instance.UpdatedAt,
                task.UpdatedAt,
                variables));
        }
        return result;
    }

    private static IReadOnlyList<AdministrativeActionFlowTarget> NormalizeTargets(
        IReadOnlyList<AdministrativeActionFlowTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException(
                "At least one exact workflow-definition and flow target is required.",
                nameof(targets));
        }

        if (targets.Any(target => target.WorkflowDefinitionId <= 0
                || target.FlowId <= 0
                || target.SourceNodeId <= 0))
        {
            throw new ArgumentException(
                "Workflow definition, flow, and source node identifiers must be positive.",
                nameof(targets));
        }

        if (targets.Select(target => target.WorkflowDefinitionId).Distinct().Count()
            != targets.Count)
        {
            throw new ArgumentException(
                "Only one exact flow target may be supplied per workflow definition.",
                nameof(targets));
        }

        return targets.ToArray();
    }

    private static NpgsqlParameter[] BuildParameters(
        IEnumerable<(string Name, object Value)> arguments) =>
        arguments.Select(argument => new NpgsqlParameter(argument.Name, argument.Value)).ToArray();
}
