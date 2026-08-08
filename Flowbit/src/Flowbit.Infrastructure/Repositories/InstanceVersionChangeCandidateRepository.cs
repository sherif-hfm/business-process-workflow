using System.Text;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class InstanceVersionChangeCandidateRepository(
    AppDbContext dbContext,
    IWorkflowRuntimeRepository runtime) : IInstanceVersionChangeCandidateRepository
{
    public Task<PagedResult<InstanceListItem>> SearchAsync(
        InstanceVersionChangeCandidateQuery query,
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
            query.SourceWorkflowDefinitionId,
            workflowKey: null,
            query.BusinessKey,
            query.NodeId,
            query.NodeExternalId,
            query.VariableFilter,
            [
                new InstanceSortCriterion(InstanceSortField.UpdatedAt, SortDirection.Descending),
                new InstanceSortCriterion(InstanceSortField.Id, SortDirection.Descending)
            ],
            InstanceListAuthorization.Global,
            cursor: null,
            query.IncludeVariables,
            query.Page,
            query.PageSize,
            cancellationToken);
    }

    public async Task<IReadOnlyList<FrozenInstanceVersionChangeCandidate>> MaterializeAsync(
        InstanceVersionChangeCandidateQuery query,
        IReadOnlyCollection<long> excludedInstanceIds,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        if (limit is <= 0 or > InstanceVersionChangeBatchConstraints.MaxBatchInstances)
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
            return await dbContext.WorkflowInstances
                .AsNoTracking()
                .Where(instance =>
                    included.Contains(instance.Id)
                    && !excluded.Contains(instance.Id)
                    && instance.WorkflowDefinitionId == query.SourceWorkflowDefinitionId
                    && instance.Status == WorkflowInstanceStatuses.Running)
                .OrderBy(instance => instance.Id)
                .Take(limit + 1)
                .Select(instance => new FrozenInstanceVersionChangeCandidate(
                    instance.Id,
                    instance.WorkflowDefinitionId,
                    instance.UpdatedAt))
                .ToListAsync(cancellationToken);
        }

        var where = new StringBuilder(
            " WHERE w.\"Status\" = @status AND w.\"WorkflowDefinitionId\" = @workflowId");
        var arguments = new List<(string Name, object Value)>
        {
            ("status", WorkflowInstanceStatuses.Running),
            ("workflowId", query.SourceWorkflowDefinitionId)
        };
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
            .Select(instance => new FrozenInstanceVersionChangeCandidate(
                instance.Id,
                instance.WorkflowDefinitionId,
                instance.UpdatedAt))
            .ToArray();
    }

    private static void ValidateQuery(InstanceVersionChangeCandidateQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.SourceWorkflowDefinitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query.SourceWorkflowDefinitionId));
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
