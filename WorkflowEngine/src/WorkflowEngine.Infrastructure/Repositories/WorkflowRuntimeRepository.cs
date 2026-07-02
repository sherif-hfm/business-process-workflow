using Microsoft.EntityFrameworkCore;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Infrastructure.Entities;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;

namespace WorkflowEngine.Infrastructure.Repositories;

public sealed class WorkflowRuntimeRepository(AppDbContext dbContext) : IWorkflowRuntimeRepository
{
    public async Task<WorkflowInstanceRecord> AddInstanceAsync(
        long workflowDefinitionId,
        int currentStepId,
        string? startedBy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = workflowDefinitionId,
            CurrentStepId = currentStepId,
            Status = WorkflowInstanceStatuses.Running,
            StartedBy = startedBy,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.WorkflowInstances.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<WorkflowInstanceRecord>> ListInstancesAsync(
        string? status,
        CancellationToken cancellationToken)
    {
        var query = dbContext.WorkflowInstances.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(i => i.Status == status);
        }

        var entities = await query
            .OrderByDescending(i => i.UpdatedAt)
            .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
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
