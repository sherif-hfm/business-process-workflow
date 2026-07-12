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
            Status = WorkflowInstanceStatuses.Running,
            StartedBy = startedBy,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.WorkflowInstances.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        // The start node is pass-through and the transaction is not externally
        // visible yet. Keep its position in the returned record and create the
        // persisted token at the first resolved node, avoiding an insert followed
        // immediately by an update for every new instance.
        var transientToken = NewToken(entity, node, now);
        return ToRecord(entity, transientToken, null);
    }

    // EF1002: the SQL is assembled from static fragments plus @paramName placeholders
    // only; every caller-supplied name/value is bound via NpgsqlParameter, so there is
    // no interpolation of untrusted input and no injection surface.
#pragma warning disable EF1002
    public async Task<PagedResult<InstanceListItem>> ListInstancesAsync(
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
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
                $"SELECT COUNT(*) AS \"Value\" FROM workflow_instances w JOIN LATERAL (SELECT * FROM execution_tokens et WHERE et.\"InstanceId\" = w.\"Id\" ORDER BY et.\"Id\" DESC LIMIT 1) t ON TRUE{where}",
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
                $"SELECT w.* FROM workflow_instances w JOIN LATERAL (SELECT * FROM execution_tokens et WHERE et.\"InstanceId\" = w.\"Id\" ORDER BY et.\"Id\" DESC LIMIT 1) t ON TRUE{where} ORDER BY w.\"UpdatedAt\" DESC, w.\"Id\" DESC LIMIT @take OFFSET @skip",
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
        string? workflowKey,
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
                $"SELECT COUNT(*) AS \"Value\" FROM workflow_instances w JOIN user_tasks ut ON ut.\"InstanceId\" = w.\"Id\"{where}",
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
                $"SELECT w.* FROM workflow_instances w JOIN user_tasks ut ON ut.\"InstanceId\" = w.\"Id\"{where} ORDER BY ut.\"UpdatedAt\" DESC, ut.\"Id\" DESC LIMIT @take OFFSET @skip",
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
        string? workflowKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters,
        CancellationToken cancellationToken)
    {
        var (where, args) = BuildInboxWhere(user, roles, instanceId, workflowId, workflowKey, nodeId, nodeExternalId, variableFilters);

        var entities = await dbContext.WorkflowInstances
            .FromSqlRaw(
                $"SELECT w.* FROM workflow_instances w JOIN user_tasks ut ON ut.\"InstanceId\" = w.\"Id\"{where} ORDER BY ut.\"UpdatedAt\" DESC, ut.\"Id\" DESC",
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
        string? workflowKey,
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
              AND ut."Status" = @activeTask
              AND (
                    ut."ClaimedBy" = @user
                 OR (
                      ( cardinality(ut."Roles") = 0
                        OR EXISTS (
                            SELECT 1 FROM unnest(ut."Roles") AS node_role
                            WHERE lower(node_role) = ANY(@lowerRoles)
                        ) )
                      AND NOT (ut."RequiresClaim"
                               AND ut."ClaimedBy" IS NOT NULL
                               AND ut."ClaimedBy" <> @user)
                    )
                  )
            """);

        var args = new List<(string Name, object Value)>
        {
            ("status", WorkflowInstanceStatuses.Running),
            ("activeTask", UserTaskStatuses.Active),
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
        string? workflowKey)
    {
        if (workflowKey is null)
        {
            return;
        }

        args.Add(("workflowKey", workflowKey));
        where.Append(
            " AND EXISTS (SELECT 1 FROM workflow_definitions d" +
            " WHERE d.\"Id\" = w.\"WorkflowDefinitionId\" AND d.\"WorkflowKey\" = @workflowKey)");
    }

    // Filters on the token/task node id. The value is parameter-bound,
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
        where.Append(where.ToString().Contains("ut.\"Status\"")
            ? " AND ut.\"NodeId\" = @nodeId"
            : " AND t.\"NodeId\" = @nodeId");
    }

    // Filters on the projected token/task externalId (exact, case-insensitive).
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
        where.Append(where.ToString().Contains("ut.\"Status\"")
            ? " AND lower(ut.\"NodeExternalId\") = lower(@nodeExternalId)"
            : " AND lower(t.\"NodeExternalId\") = lower(@nodeExternalId)");
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

        var instanceIds = entities.Select(e => e.Id).Distinct().ToList();
        var tokens = await dbContext.ExecutionTokens.AsNoTracking()
            .Where(t => instanceIds.Contains(t.InstanceId))
            .OrderByDescending(t => t.Id)
            .ToListAsync(cancellationToken);
        var latestTokens = tokens
            .GroupBy(t => t.InstanceId)
            .ToDictionary(g => g.Key, g => g.First());
        var activeTasks = await dbContext.UserTasks.AsNoTracking()
            .Where(t => instanceIds.Contains(t.InstanceId) && t.Status == UserTaskStatuses.Active)
            .OrderByDescending(t => t.Id)
            .ToListAsync(cancellationToken);
        var tasksByInstance = activeTasks
            .GroupBy(t => t.InstanceId)
            .ToDictionary(g => g.Key, g => g.First());

        return entities.Select(e =>
        {
            definitions.TryGetValue(e.WorkflowDefinitionId, out var definition);
            if (!latestTokens.TryGetValue(e.Id, out var token))
            {
                throw new InvalidOperationException($"Workflow instance #{e.Id} has no execution token.");
            }
            tasksByInstance.TryGetValue(e.Id, out var task);
            return new InstanceListItem(
                e.Id,
                definition?.Id ?? 0,
                e.WorkflowDefinitionId,
                definition?.Name ?? string.Empty,
                definition?.Version ?? 0,
                token.Id,
                task?.Id,
                token.NodeId,
                token.NodeName,
                token.NodeExternalId,
                token.NodeType,
                task?.Roles ?? [],
                task?.RequiresClaim ?? false,
                e.Status,
                task?.ClaimedBy,
                e.StartedBy,
                e.CreatedAt,
                e.UpdatedAt);
        }).ToList();
    }

    public async Task<WorkflowInstanceRecord?> GetInstanceAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowInstances.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var token = await dbContext.ExecutionTokens.AsNoTracking()
            .Where(t => t.InstanceId == id)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var task = await dbContext.UserTasks.AsNoTracking()
            .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return token is null ? null : ToRecord(entity, token, task);
    }

    public async Task<WorkflowInstanceRecord?> GetInstanceForUpdateAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowInstances
            .FromSqlInterpolated($"SELECT * FROM workflow_instances WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var token = await dbContext.ExecutionTokens
            .FromSqlInterpolated($"SELECT * FROM execution_tokens WHERE \"InstanceId\" = {id} ORDER BY \"Id\" DESC LIMIT 1 FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var task = await dbContext.UserTasks
            .FromSqlInterpolated($"SELECT * FROM user_tasks WHERE \"InstanceId\" = {id} AND \"Status\" = {UserTaskStatuses.Active} ORDER BY \"Id\" DESC LIMIT 1 FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        return token is null ? null : ToRecord(entity, token, task);
    }

    public async Task UpdateInstanceAsync(
        long id,
        int currentStepId,
        string status,
        string? claimedBy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var trackedEntity = dbContext.WorkflowInstances.Local.SingleOrDefault(i => i.Id == id);
        var entity = trackedEntity
            ?? await dbContext.WorkflowInstances.SingleAsync(i => i.Id == id, cancellationToken);
        entity.Status = status;
        entity.UpdatedAt = now;

        var trackedToken = dbContext.ExecutionTokens.Local
            .Where(t => t.InstanceId == id)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault();
        var token = trackedToken;
        if (token is null)
        {
            token = await dbContext.ExecutionTokens
                .Where(t => t.InstanceId == id)
                .OrderByDescending(t => t.Id)
                .FirstAsync(cancellationToken);
        }

        var trackedTask = dbContext.UserTasks.Local
            .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault();
        // AddInstanceAsync and GetInstanceForUpdateAsync both preload the complete
        // active execution state. When the instance is tracked, a missing local task
        // therefore means there is no active user task; querying again would add one
        // database round trip to every automatic pass-through hop.
        var task = trackedTask;
        if (trackedEntity is null && task is null)
        {
            task = await dbContext.UserTasks
                .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (status == WorkflowInstanceStatuses.Running)
        {
            if (token.NodeId != currentStepId)
            {
                throw new InvalidOperationException("UpdateInstanceAsync cannot move an execution token; use UpdateInstanceNodeAsync.");
            }
            if (task is not null)
            {
                task.ClaimedBy = claimedBy;
                task.UpdatedAt = now;
            }
            return;
        }

        token.Status = ToTokenStatus(status);
        token.UpdatedAt = now;
        CompleteTask(task, status == WorkflowInstanceStatuses.Cancelled, now);
    }

    public async Task UpdateInstanceNodeAsync(
        long id,
        CurrentNodeSnapshot node,
        string status,
        string? claimedBy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var trackedEntity = dbContext.WorkflowInstances.Local.SingleOrDefault(i => i.Id == id);
        var entity = trackedEntity
            ?? await dbContext.WorkflowInstances.SingleAsync(i => i.Id == id, cancellationToken);
        var trackedToken = dbContext.ExecutionTokens.Local
            .Where(t => t.InstanceId == id)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault();
        var token = trackedToken;
        if (token is null && trackedEntity is not null)
        {
            token = NewToken(entity, node, now);
            dbContext.ExecutionTokens.Add(token);
        }
        else if (token is null)
        {
            token = await dbContext.ExecutionTokens
                .Where(t => t.InstanceId == id)
                .OrderByDescending(t => t.Id)
                .FirstAsync(cancellationToken);
        }
        var trackedTask = dbContext.UserTasks.Local
            .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault();
        var task = trackedTask;
        if (trackedEntity is null && task is null)
        {
            task = await dbContext.UserTasks
                .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        CompleteTask(task, status == WorkflowInstanceStatuses.Cancelled, now);
        token.NodeId = node.Id;
        token.NodeName = node.Name;
        token.NodeExternalId = node.ExternalId;
        token.NodeType = node.Type;
        token.Status = ToTokenStatus(status);
        token.UpdatedAt = now;
        entity.Status = status;
        entity.UpdatedAt = now;

        if (status == WorkflowInstanceStatuses.Running && node.Type == BpmnFlowNodeTypes.UserTask)
        {
            dbContext.UserTasks.Add(NewUserTask(entity, token, node, now, claimedBy));
        }
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

    public async Task AcquireStartLockAsync(string workflowKey, string idempotencyKeyValue, CancellationToken cancellationToken)
    {
        // pg_advisory_xact_lock is held until the current transaction commits or
        // rolls back, serializing concurrent message-start deliveries carrying the
        // same (workflowKey, idempotency key) so the dedupe-by-variable check is
        // race-free. The hash is computed by Postgres' hashtext(), which is cluster-
        // stable, so lock keys are identical across API replicas (unlike .NET's
        // per-process-randomized GetHashCode). Collisions merely cause harmless
        // extra serialization.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({workflowKey}), hashtext({idempotencyKeyValue}))", cancellationToken);
    }

    private static WorkflowInstanceRecord ToRecord(
        WorkflowInstanceEntity entity,
        ExecutionTokenEntity token,
        UserTaskEntity? task) =>
        new(
            entity.Id,
            entity.WorkflowDefinitionId,
            token.Id,
            token.NodeId,
            task?.Id,
            entity.Status,
            task?.ClaimedBy,
            entity.StartedBy,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static ExecutionTokenEntity NewToken(
        WorkflowInstanceEntity instance,
        CurrentNodeSnapshot node,
        DateTimeOffset now) =>
        new()
        {
            Instance = instance,
            NodeId = node.Id,
            NodeName = node.Name,
            NodeExternalId = node.ExternalId,
            NodeType = node.Type,
            Status = ExecutionTokenStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static UserTaskEntity NewUserTask(
        WorkflowInstanceEntity instance,
        ExecutionTokenEntity token,
        CurrentNodeSnapshot node,
        DateTimeOffset now,
        string? claimedBy = null) =>
        new()
        {
            Instance = instance,
            Token = token,
            NodeId = node.Id,
            NodeName = node.Name,
            NodeExternalId = node.ExternalId,
            Roles = node.Roles.ToList(),
            RequiresClaim = node.RequiresClaim,
            Status = UserTaskStatuses.Active,
            ClaimedBy = claimedBy,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static void CompleteTask(UserTaskEntity? task, bool cancelled, DateTimeOffset now)
    {
        if (task is null)
        {
            return;
        }

        task.Status = cancelled ? UserTaskStatuses.Cancelled : UserTaskStatuses.Completed;
        task.CompletedAt = now;
        task.UpdatedAt = now;
    }

    private static string ToTokenStatus(string instanceStatus) => instanceStatus switch
    {
        WorkflowInstanceStatuses.Running => ExecutionTokenStatuses.Active,
        WorkflowInstanceStatuses.Completed => ExecutionTokenStatuses.Completed,
        WorkflowInstanceStatuses.Faulted => ExecutionTokenStatuses.Faulted,
        WorkflowInstanceStatuses.Cancelled => ExecutionTokenStatuses.Cancelled,
        _ => throw new InvalidOperationException($"Unknown workflow instance status '{instanceStatus}'.")
    };

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
