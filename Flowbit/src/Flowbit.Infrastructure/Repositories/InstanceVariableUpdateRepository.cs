using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

public sealed class InstanceVariableUpdateRepository(AppDbContext dbContext)
    : IInstanceVariableUpdateRepository
{
    public async Task<InstanceVariableUpdateAuditRecord?> GetAsync(
        long operationId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.InstanceVariableUpdates
            .AsNoTracking()
            .SingleOrDefaultAsync(update => update.Id == operationId, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<InstanceVariableUpdateAuditRecord?> FindByIdempotencyKeyAsync(
        long instanceId,
        string performedBy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.InstanceVariableUpdates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                update => update.InstanceId == instanceId
                    && update.PerformedBy == performedBy
                    && update.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public Task LockIdempotencyKeyAsync(
        long instanceId,
        string performedBy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var lockKey = $"{instanceId}\u001f{performedBy}\u001f{idempotencyKey}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('instance-variable-update'), hashtext({lockKey}))",
            cancellationToken);
    }

    public async Task<InstanceVariableUpdateAuditRecord> AddAsync(
        NewInstanceVariableUpdateAuditRecord create,
        CancellationToken cancellationToken)
    {
        var entity = new InstanceVariableUpdateAuditEntity
        {
            InstanceId = create.InstanceId,
            WorkflowDefinitionId = create.WorkflowDefinitionId,
            PerformedBy = create.PerformedBy,
            PerformedByRolesJson = JsonMapping.ToJsonDocument(create.PerformedByRoles),
            Reason = create.Reason,
            RequestedVariablesJson = JsonMapping.ToJsonDocument(create.RequestedVariables),
            ResultJson = JsonDocument.Parse("{}"),
            IdempotencyKey = create.IdempotencyKey,
            BatchId = create.BatchId,
            BatchItemId = create.BatchItemId,
            PerformedAt = create.PerformedAt
        };
        dbContext.InstanceVariableUpdates.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<InstanceVariableUpdateAuditRecord> SetResultAsync(
        long operationId,
        JsonElement result,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.InstanceVariableUpdates.Local
            .SingleOrDefault(update => update.Id == operationId)
            ?? await dbContext.InstanceVariableUpdates
                .SingleAsync(update => update.Id == operationId, cancellationToken);
        entity.ResultJson = JsonMapping.ToJsonDocument(result);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<InstanceVariableUpdateVariableRecord>> ListVariablesAsync(
        long operationId,
        CancellationToken cancellationToken)
    {
        var variables = await dbContext.InstanceVariables
            .AsNoTracking()
            .Where(variable => variable.InstanceVariableUpdateAuditId == operationId)
            .OrderBy(variable => variable.Id)
            .ToListAsync(cancellationToken);
        return variables.Select(variable => new InstanceVariableUpdateVariableRecord(
                variable.Id,
                variable.VariableName,
                variable.ValueJson.RootElement.Clone()))
            .ToArray();
    }

    public async Task<IReadOnlyList<InstanceVariableUpdateAuditRecord>> ListByInstanceAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.InstanceVariableUpdates
            .AsNoTracking()
            .Where(update => update.InstanceId == instanceId)
            .OrderBy(update => update.PerformedAt)
            .ThenBy(update => update.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToArray();
    }

    private static InstanceVariableUpdateAuditRecord ToRecord(
        InstanceVariableUpdateAuditEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.WorkflowDefinitionId,
            entity.PerformedBy,
            JsonMapping.ToStringList(entity.PerformedByRolesJson) ?? [],
            entity.Reason,
            entity.RequestedVariablesJson.RootElement.Clone(),
            entity.ResultJson.RootElement.Clone(),
            entity.IdempotencyKey,
            entity.BatchId,
            entity.BatchItemId,
            entity.PerformedAt);
}
