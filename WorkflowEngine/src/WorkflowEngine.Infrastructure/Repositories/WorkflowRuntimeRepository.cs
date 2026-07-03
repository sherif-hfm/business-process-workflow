using Microsoft.EntityFrameworkCore;
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

    public async Task<PagedResult<InstanceListItem>> ListInstancesAsync(
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.WorkflowInstances.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(i => i.Status == status);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);

        var entities = await query
            .OrderByDescending(i => i.UpdatedAt)
            .ThenByDescending(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = await ToListItemsAsync(entities, cancellationToken);
        return new PagedResult<InstanceListItem>(items, page, pageSize, totalCount);
    }

    public async Task<PagedResult<InstanceListItem>> ListInboxAsync(
        string user,
        IReadOnlyCollection<string> roles,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // Roles are matched case-insensitively (mirrors the in-memory role check),
        // so compare lower-cased node roles against lower-cased actor roles.
        var lowerRoles = roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        var running = WorkflowInstanceStatuses.Running;
        var userTask = BpmnFlowNodeTypes.UserTask;

        var totalCount = await dbContext.Database
            .SqlQuery<long>($"""
                SELECT COUNT(*) AS "Value"
                FROM workflow_instances
                WHERE "Status" = {running}
                  AND "CurrentNodeType" = {userTask}
                  AND (
                        "ClaimedBy" = {user}
                     OR (
                          ( cardinality("CurrentNodeRoles") = 0
                            OR EXISTS (
                                SELECT 1 FROM unnest("CurrentNodeRoles") AS node_role
                                WHERE lower(node_role) = ANY({lowerRoles})
                            ) )
                          AND NOT ("CurrentRequiresClaim"
                                   AND "ClaimedBy" IS NOT NULL
                                   AND "ClaimedBy" <> {user})
                        )
                      )
                """)
            .SingleAsync(cancellationToken);

        var skip = (page - 1) * pageSize;
        var entities = await dbContext.WorkflowInstances
            .FromSqlInterpolated($"""
                SELECT * FROM workflow_instances
                WHERE "Status" = {running}
                  AND "CurrentNodeType" = {userTask}
                  AND (
                        "ClaimedBy" = {user}
                     OR (
                          ( cardinality("CurrentNodeRoles") = 0
                            OR EXISTS (
                                SELECT 1 FROM unnest("CurrentNodeRoles") AS node_role
                                WHERE lower(node_role) = ANY({lowerRoles})
                            ) )
                          AND NOT ("CurrentRequiresClaim"
                                   AND "ClaimedBy" IS NOT NULL
                                   AND "ClaimedBy" <> {user})
                        )
                      )
                ORDER BY "UpdatedAt" DESC, "Id" DESC
                LIMIT {pageSize} OFFSET {skip}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var items = await ToListItemsAsync(entities, cancellationToken);
        return new PagedResult<InstanceListItem>(items, page, pageSize, totalCount);
    }

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
                e.WorkflowDefinitionId,
                definition?.Name ?? string.Empty,
                definition?.Version ?? 0,
                e.CurrentStepId,
                e.CurrentNodeName,
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
        System.Text.Json.JsonElement value,
        CancellationToken cancellationToken)
    {
        dbContext.InstanceVariables.Add(new InstanceVariableEntity
        {
            InstanceId = instanceId,
            VariableName = variableName,
            SourceActionId = sourceActionId,
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
