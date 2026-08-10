using System.Text;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class InstanceVariableUpdateCandidateRepository(
    AppDbContext dbContext,
    IWorkflowRuntimeRepository runtime) : IInstanceVariableUpdateCandidateRepository
{
    public Task<PagedResult<InstanceListItem>> SearchAsync(
        InstanceVariableUpdateCandidateQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        if (query.InstanceIds is { Count: > 0 })
        {
            throw new ArgumentException(
                "Candidate search accepts one instance id; instance-id collections are reserved for materialization.",
                nameof(query));
        }

        return runtime.ListInstancesAsync(
            WorkflowInstanceStatuses.Running,
            query.InstanceId,
            query.WorkflowDefinitionId,
            query.WorkflowKey,
            query.BusinessKey,
            query.NodeId,
            query.NodeExternalId,
            query.VariableFilter,
            query.Sort,
            InstanceListAuthorization.Global,
            query.Cursor,
            query.IncludeVariables,
            query.Page,
            query.PageSize,
            cancellationToken);
    }

    public async Task<IReadOnlyList<FrozenInstanceVariableUpdateCandidate>> MaterializeAsync(
        InstanceVariableUpdateCandidateQuery query,
        IReadOnlyCollection<long> excludedInstanceIds,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        if (limit is <= 0 or > InstanceVariableUpdateConstraints.MaxBatchInstances)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var excluded = excludedInstanceIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (query.InstanceIds is { Count: > 0 } explicitIds)
        {
            if (query.InstanceId is not null)
            {
                throw new ArgumentException(
                    "Candidate materialization cannot combine InstanceId and InstanceIds.",
                    nameof(query));
            }

            var included = explicitIds
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            var candidates = dbContext.WorkflowInstances
                .AsNoTracking()
                .Where(instance =>
                    included.Contains(instance.Id)
                    && !excluded.Contains(instance.Id)
                    && instance.WorkflowKey == query.WorkflowKey
                    && instance.Status == WorkflowInstanceStatuses.Running);
            if (query.WorkflowDefinitionId is long workflowDefinitionId)
            {
                candidates = candidates.Where(instance =>
                    instance.WorkflowDefinitionId == workflowDefinitionId);
            }

            return await candidates
                .OrderBy(instance => instance.Id)
                .Take(limit + 1)
                .Select(instance => new FrozenInstanceVariableUpdateCandidate(
                    instance.Id,
                    instance.WorkflowDefinitionId,
                    instance.BusinessKey,
                    instance.UpdatedAt))
                .ToListAsync(cancellationToken);
        }

        var where = new StringBuilder(
            " WHERE w.\"Status\" = @status AND w.\"WorkflowKey\" = @workflowKey");
        var arguments = new List<(string Name, object Value)>
        {
            ("status", WorkflowInstanceStatuses.Running),
            ("workflowKey", query.WorkflowKey)
        };
        if (query.WorkflowDefinitionId is long definitionId)
        {
            where.Append(" AND w.\"WorkflowDefinitionId\" = @workflowId");
            arguments.Add(("workflowId", definitionId));
        }
        if (query.InstanceId is long instanceId)
        {
            where.Append(" AND w.\"Id\" = @instanceId");
            arguments.Add(("instanceId", instanceId));
        }
        if (!string.IsNullOrWhiteSpace(query.BusinessKey))
        {
            where.Append(" AND w.\"BusinessKey\" = @businessKey");
            arguments.Add(("businessKey", query.BusinessKey.Trim()));
        }
        if (query.NodeId is int nodeId)
        {
            where.Append(
                " AND EXISTS (SELECT 1 FROM flowbit.execution_tokens position "
                + "WHERE position.\"InstanceId\" = w.\"Id\" "
                + "AND position.\"Status\" = 'active' AND position.\"NodeId\" = @nodeId)");
            arguments.Add(("nodeId", nodeId));
        }
        if (!string.IsNullOrWhiteSpace(query.NodeExternalId))
        {
            where.Append(
                " AND EXISTS (SELECT 1 FROM flowbit.execution_tokens position "
                + "WHERE position.\"InstanceId\" = w.\"Id\" "
                + "AND position.\"Status\" = 'active' "
                + "AND lower(position.\"NodeExternalId\") = lower(@nodeExternalId))");
            arguments.Add(("nodeExternalId", query.NodeExternalId.Trim()));
        }
        VariableFilterSqlCompiler.Append(where, arguments, query.VariableFilter, "w.\"Id\"");
        if (excluded.Length > 0)
        {
            where.Append(" AND NOT (w.\"Id\" = ANY(@excludedIds))");
            arguments.Add(("excludedIds", excluded));
        }
        arguments.Add(("take", limit + 1));

#pragma warning disable EF1002
        var materialized = await dbContext.WorkflowInstances
            .FromSqlRaw(
                $"SELECT w.* FROM flowbit.workflow_instances AS w {where} ORDER BY w.\"Id\" LIMIT @take",
                BuildParameters(arguments))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002
        return materialized
            .Select(instance => new FrozenInstanceVariableUpdateCandidate(
                instance.Id,
                instance.WorkflowDefinitionId,
                instance.BusinessKey,
                instance.UpdatedAt))
            .ToArray();
    }

    private static void ValidateQuery(InstanceVariableUpdateCandidateQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.WorkflowKey))
        {
            throw new ArgumentException("WorkflowKey is required.", nameof(query));
        }
        if (query.WorkflowDefinitionId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query.WorkflowDefinitionId));
        }
        if (query.InstanceId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query.InstanceId));
        }
        if (query.Page <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query.Page));
        }
        if (query.PageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query.PageSize));
        }
    }

    private static NpgsqlParameter[] BuildParameters(
        IEnumerable<(string Name, object Value)> arguments) =>
        arguments.Select(argument => new NpgsqlParameter(argument.Name, argument.Value)).ToArray();
}
