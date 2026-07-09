using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Infrastructure.Entities;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Infrastructure.Repositories;

public sealed class WorkflowRuntimeRepository(AppDbContext dbContext) : IWorkflowRuntimeRepository
{
    public async Task<WorkflowInstanceRecord> AddInstanceAsync(
        long workflowDefinitionId,
        CurrentNodeSnapshot node,
        string? startedBy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = workflowDefinitionId,
            CurrentStepId = node.Id,
            CurrentNodeName = node.Name,
            CurrentNodeExternalId = node.ExternalId,
            CurrentNodeType = node.Type,
            CurrentNodeRoles = node.Roles.ToList(),
            CurrentRequiresClaim = node.RequiresClaim,
            Status = WorkflowInstanceStatuses.Running,
            StartedBy = startedBy,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.WorkflowInstances.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    // EF1002: the SQL is assembled from static fragments plus @paramName placeholders
    // only; every caller-supplied name/value is bound via NpgsqlParameter, so there is
    // no interpolation of untrusted input and no injection surface.
#pragma warning disable EF1002
    public async Task<PagedResult<InstanceListItem>> ListInstancesAsync(
        string? status,
        long? instanceId,
        long? workflowId,
        int? workflowKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var where = new StringBuilder(" WHERE 1=1");
        var args = new List<(string Name, object Value)>();

        if (!string.IsNullOrWhiteSpace(status))
        {
            args.Add(("status", status));
            where.Append(" AND w.\"Status\" = @status");
        }

        AppendInstanceIdFilter(where, args, instanceId);
        AppendWorkflowIdFilter(where, args, workflowId);
        AppendWorkflowKeyFilter(where, args, workflowKey);
        AppendNodeIdFilter(where, args, nodeId);
        AppendNodeExternalIdFilter(where, args, nodeExternalId);
        AppendVariableFilters(where, args, variableFilters);

        var totalCount = await dbContext.Database
            .SqlQueryRaw<long>(
                $"SELECT COUNT(*) AS \"Value\" FROM workflow_instances w{where}",
                BuildParameters(args))
            .SingleAsync(cancellationToken);

        var skip = (page - 1) * pageSize;
        var pageArgs = new List<(string Name, object Value)>(args)
        {
            ("take", pageSize),
            ("skip", skip)
        };
        var entities = await dbContext.WorkflowInstances
            .FromSqlRaw(
                $"SELECT * FROM workflow_instances w{where} ORDER BY w.\"UpdatedAt\" DESC, w.\"Id\" DESC LIMIT @take OFFSET @skip",
                BuildParameters(pageArgs))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var items = await ToListItemsAsync(entities, cancellationToken);
        return new PagedResult<InstanceListItem>(items, page, pageSize, totalCount);
    }

    public async Task<PagedResult<InstanceListItem>> ListInboxAsync(
        string user,
        IReadOnlyCollection<string> roles,
        long? instanceId,
        long? workflowId,
        int? workflowKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // Roles are matched case-insensitively (mirrors the in-memory role check),
        // so compare lower-cased node roles against lower-cased actor roles.
        var (where, args) = BuildInboxWhere(user, roles, instanceId, workflowId, workflowKey, nodeId, nodeExternalId, variableFilters);

        var totalCount = await dbContext.Database
            .SqlQueryRaw<long>(
                $"SELECT COUNT(*) AS \"Value\" FROM workflow_instances w{where}",
                BuildParameters(args))
            .SingleAsync(cancellationToken);

        var skip = (page - 1) * pageSize;
        var pageArgs = new List<(string Name, object Value)>(args)
        {
            ("take", pageSize),
            ("skip", skip)
        };
        var entities = await dbContext.WorkflowInstances
            .FromSqlRaw(
                $"SELECT * FROM workflow_instances w{where} ORDER BY w.\"UpdatedAt\" DESC, w.\"Id\" DESC LIMIT @take OFFSET @skip",
                BuildParameters(pageArgs))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var items = await ToListItemsAsync(entities, cancellationToken);
        return new PagedResult<InstanceListItem>(items, page, pageSize, totalCount);
    }

    public async Task<IReadOnlyList<InstanceListItem>> ListInboxCandidatesAsync(
        string user,
        IReadOnlyCollection<string> roles,
        long? instanceId,
        long? workflowId,
        int? workflowKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters,
        CancellationToken cancellationToken)
    {
        var (where, args) = BuildInboxWhere(user, roles, instanceId, workflowId, workflowKey, nodeId, nodeExternalId, variableFilters);

        var entities = await dbContext.WorkflowInstances
            .FromSqlRaw(
                $"SELECT * FROM workflow_instances w{where} ORDER BY w.\"UpdatedAt\" DESC, w.\"Id\" DESC",
                BuildParameters(args))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return await ToListItemsAsync(entities, cancellationToken);
    }

    private static (StringBuilder Where, List<(string Name, object Value)> Args) BuildInboxWhere(
        string user,
        IReadOnlyCollection<string> roles,
        long? instanceId,
        long? workflowId,
        int? workflowKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters)
    {
        // Roles are matched case-insensitively (mirrors the in-memory role check),
        // so compare lower-cased node roles against lower-cased actor roles.
        var lowerRoles = roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        // Actor-scoped inbox predicate (running user tasks the caller may see/act on).
        // Aliased as w so the variable EXISTS filters can correlate on w."Id".
        var where = new StringBuilder("""
             WHERE w."Status" = @status
              AND w."CurrentNodeType" = @userTask
              AND (
                    w."ClaimedBy" = @user
                 OR (
                      ( cardinality(w."CurrentNodeRoles") = 0
                        OR EXISTS (
                            SELECT 1 FROM unnest(w."CurrentNodeRoles") AS node_role
                            WHERE lower(node_role) = ANY(@lowerRoles)
                        ) )
                      AND NOT (w."CurrentRequiresClaim"
                               AND w."ClaimedBy" IS NOT NULL
                               AND w."ClaimedBy" <> @user)
                    )
                  )
            """);

        var args = new List<(string Name, object Value)>
        {
            ("status", WorkflowInstanceStatuses.Running),
            ("userTask", BpmnFlowNodeTypes.UserTask),
            ("user", user),
            ("lowerRoles", lowerRoles)
        };

        AppendInstanceIdFilter(where, args, instanceId);
        AppendWorkflowIdFilter(where, args, workflowId);
        AppendWorkflowKeyFilter(where, args, workflowKey);
        AppendNodeIdFilter(where, args, nodeId);
        AppendNodeExternalIdFilter(where, args, nodeExternalId);
        AppendVariableFilters(where, args, variableFilters);

        return (where, args);
    }
#pragma warning restore EF1002

    // Filters on the instance id (primary key). The value is parameter-bound,
    // so there is no SQL injection surface.
    private static void AppendInstanceIdFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        long? instanceId)
    {
        if (instanceId is null)
        {
            return;
        }

        args.Add(("instanceId", instanceId.Value));
        where.Append(" AND w.\"Id\" = @instanceId");
    }

    // Filters on the owning workflow definition id. The value is parameter-bound,
    // so there is no SQL injection surface.
    private static void AppendWorkflowIdFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        long? workflowId)
    {
        if (workflowId is null)
        {
            return;
        }

        args.Add(("workflowId", workflowId.Value));
        where.Append(" AND w.\"WorkflowDefinitionId\" = @workflowId");
    }

    // Filters on the stable, cross-version workflow key (the JSON model id stored on
    // workflow_definitions), matched via a correlated EXISTS against the instance's
    // definition. Because every version shares the key, this spans all versions. The
    // value is parameter-bound, so there is no SQL injection surface.
    private static void AppendWorkflowKeyFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        int? workflowKey)
    {
        if (workflowKey is null)
        {
            return;
        }

        args.Add(("workflowKey", workflowKey.Value));
        where.Append(
            " AND EXISTS (SELECT 1 FROM workflow_definitions d" +
            " WHERE d.\"Id\" = w.\"WorkflowDefinitionId\" AND d.\"WorkflowKey\" = @workflowKey)");
    }

    // Filters on the resting-node id (CurrentStepId). The value is parameter-bound,
    // so there is no SQL injection surface.
    private static void AppendNodeIdFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        int? nodeId)
    {
        if (nodeId is null)
        {
            return;
        }

        args.Add(("nodeId", nodeId.Value));
        where.Append(" AND w.\"CurrentStepId\" = @nodeId");
    }

    // Filters on the denormalized resting-node externalId (exact, case-insensitive).
    // The value is parameter-bound, so there is no SQL injection surface.
    private static void AppendNodeExternalIdFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        string? nodeExternalId)
    {
        if (string.IsNullOrWhiteSpace(nodeExternalId))
        {
            return;
        }

        args.Add(("nodeExternalId", nodeExternalId.Trim()));
        where.Append(" AND lower(w.\"CurrentNodeExternalId\") = lower(@nodeExternalId)");
    }

    // Appends one correlated EXISTS per filter: the variable must exist on the
    // instance with a scalar value equal (case-insensitively) to the target.
    // Names/values bind as parameters, so there is no SQL injection surface.
    // `#>> ARRAY[]::text[]` extracts the root scalar as text (equivalent to the
    // `#>> '{}'` literal but brace-free, since FromSqlRaw runs string.Format
    // over the SQL and would treat literal braces as format placeholders).
    private static void AppendVariableFilters(
        StringBuilder where,
        List<(string Name, object Value)> args,
        IReadOnlyList<VariableFilter> filters)
    {
        for (var i = 0; i < filters.Count; i++)
        {
            args.Add(($"vn{i}", filters[i].Name));
            args.Add(($"vv{i}", filters[i].Value));
            where.Append(
                $" AND EXISTS (SELECT 1 FROM instance_variables v WHERE v.\"InstanceId\" = w.\"Id\"" +
                $" AND v.\"VariableName\" = @vn{i} AND lower(v.\"ValueJson\" #>> ARRAY[]::text[]) = lower(@vv{i}))");
        }
    }

    private static NpgsqlParameter[] BuildParameters(IEnumerable<(string Name, object Value)> args) =>
        args.Select(a => new NpgsqlParameter(a.Name, a.Value)).ToArray();

    private async Task<IReadOnlyList<InstanceListItem>> ToListItemsAsync(
        IReadOnlyList<WorkflowInstanceEntity> entities,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
        {
            return [];
        }

        // Fetch only the definition name/version (never the JSONB body) for the
        // bounded set of definitions referenced by this page.
        var definitionIds = entities.Select(e => e.WorkflowDefinitionId).Distinct().ToList();
        var definitions = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Name, d.Version })
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        return entities.Select(e =>
        {
            definitions.TryGetValue(e.WorkflowDefinitionId, out var definition);
            return new InstanceListItem(
                e.Id,
                definition?.Id ?? 0,
                e.WorkflowDefinitionId,
                definition?.Name ?? string.Empty,
                definition?.Version ?? 0,
                e.CurrentStepId,
                e.CurrentNodeName,
                e.CurrentNodeExternalId,
                e.CurrentNodeType,
                e.CurrentNodeRoles,
                e.CurrentRequiresClaim,
                e.Status,
                e.ClaimedBy,
                e.StartedBy,
                e.CreatedAt,
                e.UpdatedAt);
        }).ToList();
    }

    public async Task<WorkflowInstanceRecord?> GetInstanceAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowInstances.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<WorkflowInstanceRecord?> GetInstanceForUpdateAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowInstances
            .FromSqlInterpolated($"SELECT * FROM workflow_instances WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task UpdateInstanceAsync(
        long id,
        int currentStepId,
        string status,
        string? claimedBy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await dbContext.WorkflowInstances
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(i => i.CurrentStepId, currentStepId)
                    .SetProperty(i => i.Status, status)
                    .SetProperty(i => i.ClaimedBy, claimedBy)
                    .SetProperty(i => i.UpdatedAt, now),
                cancellationToken);
    }

    public async Task UpdateInstanceNodeAsync(
        long id,
        CurrentNodeSnapshot node,
        string status,
        string? claimedBy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var roles = node.Roles.ToList();
        await dbContext.WorkflowInstances
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(i => i.CurrentStepId, node.Id)
                    .SetProperty(i => i.CurrentNodeName, node.Name)
                    .SetProperty(i => i.CurrentNodeExternalId, node.ExternalId)
                    .SetProperty(i => i.CurrentNodeType, node.Type)
                    .SetProperty(i => i.CurrentNodeRoles, roles)
                    .SetProperty(i => i.CurrentRequiresClaim, node.RequiresClaim)
                    .SetProperty(i => i.Status, status)
                    .SetProperty(i => i.ClaimedBy, claimedBy)
                    .SetProperty(i => i.UpdatedAt, now),
                cancellationToken);
    }

    public Task AddVariableAsync(
        long instanceId,
        string variableName,
        int? sourceActionId,
        string? setBy,
        System.Text.Json.JsonElement value,
        CancellationToken cancellationToken)
    {
        dbContext.InstanceVariables.Add(new InstanceVariableEntity
        {
            InstanceId = instanceId,
            VariableName = variableName,
            SourceActionId = sourceActionId,
            SetBy = setBy,
            ValueJson = JsonMapping.ToJsonDocument(value),
            SetAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<InstanceVariableRecord>> ListVariablesAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.InstanceVariables.AsNoTracking()
            .Where(v => v.InstanceId == instanceId)
            .OrderBy(v => v.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<InstanceVariableRecord>> ListVariablesForInstancesAsync(
        IReadOnlyCollection<long> instanceIds,
        CancellationToken cancellationToken)
    {
        if (instanceIds.Count == 0)
        {
            return [];
        }

        var entities = await dbContext.InstanceVariables.AsNoTracking()
            .Where(v => instanceIds.Contains(v.InstanceId))
            .OrderBy(v => v.InstanceId)
            .ThenBy(v => v.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    public Task AddHistoryAsync(
        long instanceId,
        int? actionId,
        int fromStepId,
        int toStepId,
        string? performedBy,
        Dictionary<string, System.Text.Json.JsonElement>? payload,
        string? note,
        CancellationToken cancellationToken)
    {
        dbContext.InstanceHistory.Add(new InstanceHistoryEntity
        {
            InstanceId = instanceId,
            ActionId = actionId,
            FromStepId = fromStepId,
            ToStepId = toStepId,
            PerformedBy = performedBy,
            Payload = JsonMapping.ToJsonDocument(payload),
            Note = note,
            PerformedAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<InstanceHistoryRecord>> ListHistoryAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.InstanceHistory.AsNoTracking()
            .Where(h => h.InstanceId == instanceId)
            .OrderBy(h => h.PerformedAt)
            .ThenBy(h => h.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    public async Task AcquireStartLockAsync(int workflowKey, string idempotencyKeyValue, CancellationToken cancellationToken)
    {
        // pg_advisory_xact_lock is held until the current transaction commits or
        // rolls back, serializing concurrent message-start deliveries carrying the
        // same (workflowKey, idempotency key) so the dedupe-by-variable check is
        // race-free. The hash is computed by Postgres' hashtext(), which is cluster-
        // stable, so lock keys are identical across API replicas (unlike .NET's
        // per-process-randomized GetHashCode). Collisions merely cause harmless
        // extra serialization.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({workflowKey}, hashtext({idempotencyKeyValue}))", cancellationToken);
    }

    private static WorkflowInstanceRecord ToRecord(WorkflowInstanceEntity entity) =>
        new(
            entity.Id,
            entity.WorkflowDefinitionId,
            entity.CurrentStepId,
            entity.Status,
            entity.ClaimedBy,
            entity.StartedBy,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static InstanceVariableRecord ToRecord(InstanceVariableEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.VariableName,
            entity.SourceActionId,
            entity.SetBy,
            entity.ValueJson.RootElement.Clone(),
            entity.SetAt);

    private static InstanceHistoryRecord ToRecord(InstanceHistoryEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.ActionId,
            entity.FromStepId,
            entity.ToStepId,
            entity.PerformedBy,
            JsonMapping.ToDictionary(entity.Payload),
            entity.Note,
            entity.PerformedAt);
}
