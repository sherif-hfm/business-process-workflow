using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

public sealed class InstanceVersionChangeBatchRepository(AppDbContext dbContext)
    : IInstanceVersionChangeBatchRepository
{
    public async Task<InstanceVersionChangeBatchRecord> AddAsync(
        NewInstanceVersionChangeBatchRecord batch,
        CancellationToken cancellationToken)
    {
        var entity = new WorkflowInstanceVersionChangeBatchEntity
        {
            WorkflowKey = batch.WorkflowKey,
            SourceWorkflowDefinitionId = batch.SourceWorkflowDefinitionId,
            TargetWorkflowDefinitionId = batch.TargetWorkflowDefinitionId,
            Reason = batch.Reason,
            SelectionJson = JsonMapping.ToJsonDocument(batch.Selection),
            Status = InstanceVersionChangeBatchStatuses.Preparing,
            PreparedBy = batch.PreparedBy,
            PreparedByRolesJson = JsonMapping.ToJsonDocument(batch.PreparedByRoles),
            IdempotencyKey = batch.IdempotencyKey,
            CreatedAt = batch.CreatedAt,
            UpdatedAt = batch.CreatedAt
        };
        dbContext.WorkflowInstanceVersionChangeBatches.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<InstanceVersionChangeBatchItemRecord>> AddItemsAsync(
        long batchId,
        IReadOnlyCollection<NewInstanceVersionChangeBatchItemRecord> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var entities = items.Select(item => new WorkflowInstanceVersionChangeBatchItemEntity
        {
            BatchId = batchId,
            InstanceId = item.InstanceId,
            CapturedSourceWorkflowDefinitionId = item.CapturedSourceWorkflowDefinitionId,
            CapturedInstanceUpdatedAt = item.CapturedInstanceUpdatedAt,
            Status = InstanceVersionChangeBatchItemStatuses.Preparing,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.CreatedAt
        }).ToArray();
        dbContext.WorkflowInstanceVersionChangeBatchItems.AddRange(entities);
        await dbContext.SaveChangesAsync(cancellationToken);
        var businessKeys = await GetBusinessKeysAsync(entities, cancellationToken);
        return entities
            .Select(entity => ToRecord(
                entity,
                businessKeys.GetValueOrDefault(entity.InstanceId),
                auditId: null))
            .ToArray();
    }

    public async Task<InstanceVersionChangeBatchRecord?> GetAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var entity = forUpdate
            ? await dbContext.WorkflowInstanceVersionChangeBatches
                .FromSqlInterpolated(
                    $"SELECT * FROM flowbit.workflow_instance_version_change_batches WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.WorkflowInstanceVersionChangeBatches
                .AsNoTracking()
                .SingleOrDefaultAsync(batch => batch.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<InstanceVersionChangeBatchRecord?> FindByIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowInstanceVersionChangeBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(
                batch => batch.PreparedBy == preparedBy
                    && batch.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public Task LockIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var lockKey = $"{preparedBy}\u001f{idempotencyKey}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('instance-version-change-batch'), hashtext({lockKey}))",
            cancellationToken);
    }

    public async Task<PagedResult<InstanceVersionChangeBatchRecord>> ListAsync(
        InstanceVersionChangeBatchSearch search,
        CancellationToken cancellationToken)
    {
        IQueryable<WorkflowInstanceVersionChangeBatchEntity> query =
            dbContext.WorkflowInstanceVersionChangeBatches.AsNoTracking();
        if (search.WorkflowKey is not null)
        {
            query = query.Where(batch => batch.WorkflowKey == search.WorkflowKey);
        }
        if (search.SourceWorkflowDefinitionId is long sourceWorkflowDefinitionId)
        {
            query = query.Where(batch =>
                batch.SourceWorkflowDefinitionId == sourceWorkflowDefinitionId);
        }
        if (search.TargetWorkflowDefinitionId is long targetWorkflowDefinitionId)
        {
            query = query.Where(batch =>
                batch.TargetWorkflowDefinitionId == targetWorkflowDefinitionId);
        }
        if (search.Status is not null)
        {
            query = query.Where(batch => batch.Status == search.Status);
        }
        if (search.PreparedBy is not null)
        {
            query = query.Where(batch => batch.PreparedBy == search.PreparedBy);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);
        var entities = await query
            .OrderByDescending(batch => batch.UpdatedAt)
            .ThenByDescending(batch => batch.Id)
            .Skip((search.Page - 1) * search.PageSize)
            .Take(search.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<InstanceVersionChangeBatchRecord>(
            entities.Select(ToRecord).ToArray(),
            search.Page,
            search.PageSize,
            totalCount);
    }

    public async Task<InstanceVersionChangeBatchItemRecord?> GetItemAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var entity = forUpdate
            ? await dbContext.WorkflowInstanceVersionChangeBatchItems
                .FromSqlInterpolated(
                    $"SELECT * FROM flowbit.workflow_instance_version_change_batch_items WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.WorkflowInstanceVersionChangeBatchItems
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var auditId = await GetAuditIdAsync(entity.Id, cancellationToken);
        var businessKey = await GetBusinessKeyAsync(entity.InstanceId, cancellationToken);
        return ToRecord(entity, businessKey, auditId);
    }

    public async Task<PagedResult<InstanceVersionChangeBatchItemRecord>> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.WorkflowInstanceVersionChangeBatchItems
            .AsNoTracking()
            .Where(item => item.BatchId == batchId);
        if (status is not null)
        {
            query = query.Where(item => item.Status == status);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);
        var entities = await query
            .OrderBy(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var auditIds = await GetAuditIdsAsync(entities, cancellationToken);
        var businessKeys = await GetBusinessKeysAsync(entities, cancellationToken);
        return new PagedResult<InstanceVersionChangeBatchItemRecord>(
            entities.Select(entity => ToRecord(
                entity,
                businessKeys.GetValueOrDefault(entity.InstanceId),
                auditIds.TryGetValue(entity.Id, out var auditId)
                    ? auditId
                    : null)).ToArray(),
            page,
            pageSize,
            totalCount);
    }

    public async Task<IReadOnlyList<InstanceVersionChangeBatchItemRecord>> ListItemsForProcessingAsync(
        long batchId,
        IReadOnlyCollection<string> statuses,
        long? afterItemId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (statuses.Count == 0 || limit <= 0)
        {
            return [];
        }

        var query = dbContext.WorkflowInstanceVersionChangeBatchItems
            .AsNoTracking()
            .Where(item => item.BatchId == batchId && statuses.Contains(item.Status));
        if (afterItemId.HasValue)
        {
            query = query.Where(item => item.Id > afterItemId.Value);
        }

        var entities = await query
            .OrderBy(item => item.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var auditIds = await GetAuditIdsAsync(entities, cancellationToken);
        var businessKeys = await GetBusinessKeysAsync(entities, cancellationToken);
        return entities
            .Select(entity => ToRecord(
                entity,
                businessKeys.GetValueOrDefault(entity.InstanceId),
                auditIds.TryGetValue(entity.Id, out var auditId)
                    ? auditId
                    : null))
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<string, int>> CountItemsByStatusAsync(
        long batchId,
        CancellationToken cancellationToken) =>
        await dbContext.WorkflowInstanceVersionChangeBatchItems
            .AsNoTracking()
            .Where(item => item.BatchId == batchId)
            .GroupBy(item => item.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

    public Task<int> CountItemsWithWarningsAsync(
        long batchId,
        CancellationToken cancellationToken) =>
        dbContext.WorkflowInstanceVersionChangeBatchItems
            .AsNoTracking()
            .CountAsync(
                item => item.BatchId == batchId && item.WarningsJson != null,
                cancellationToken);

    public Task<int> CountStaleItemsAsync(
        long batchId,
        CancellationToken cancellationToken) =>
        dbContext.WorkflowInstanceVersionChangeBatchItems
            .AsNoTracking()
            .CountAsync(
                item => item.BatchId == batchId
                    && item.ErrorCode != null
                    && (item.Status == InstanceVersionChangeBatchItemStatuses.Ineligible
                        || item.Status == InstanceVersionChangeBatchItemStatuses.Skipped)
                    && (item.ErrorCode == "stale_since_selection"
                        || item.ErrorCode == "stale_since_preparation"
                        || item.ErrorCode == "stale"),
                cancellationToken);

    public Task<int> TransitionItemsAsync(
        long batchId,
        IReadOnlyCollection<string> fromStatuses,
        string toStatus,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (fromStatuses.Count == 0)
        {
            return Task.FromResult(0);
        }

        var query = dbContext.WorkflowInstanceVersionChangeBatchItems
            .Where(item => item.BatchId == batchId && fromStatuses.Contains(item.Status));
        if (toStatus is InstanceVersionChangeBatchItemStatuses.Failed
            or InstanceVersionChangeBatchItemStatuses.Skipped
            or InstanceVersionChangeBatchItemStatuses.Cancelled)
        {
            return query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, toStatus)
                    .SetProperty(item => item.UpdatedAt, at)
                    .SetProperty(item => item.CompletedAt, at),
                cancellationToken);
        }
        return query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, toStatus)
                .SetProperty(item => item.UpdatedAt, at),
                cancellationToken);
    }

    public Task<int> FailItemsAsync(
        long batchId,
        IReadOnlyCollection<string> fromStatuses,
        string errorCode,
        string errorDescription,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (fromStatuses.Count == 0)
        {
            return Task.FromResult(0);
        }

        return dbContext.WorkflowInstanceVersionChangeBatchItems
            .Where(item => item.BatchId == batchId && fromStatuses.Contains(item.Status))
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, InstanceVersionChangeBatchItemStatuses.Failed)
                    .SetProperty(item => item.ErrorCode, errorCode)
                    .SetProperty(item => item.ErrorDescription, errorDescription)
                    .SetProperty(item => item.UpdatedAt, at)
                    .SetProperty(item => item.CompletedAt, at),
                cancellationToken);
    }

    public async Task<InstanceVersionChangeBatchRecord> UpdateAsync(
        InstanceVersionChangeBatchUpdateRecord update,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.WorkflowInstanceVersionChangeBatches.Local
            .SingleOrDefault(batch => batch.Id == update.Id)
            ?? await dbContext.WorkflowInstanceVersionChangeBatches
                .SingleAsync(batch => batch.Id == update.Id, cancellationToken);
        entity.Status = update.Status;
        entity.ConfirmedBy = update.ConfirmedBy;
        entity.ConfirmedByRolesJson = update.ConfirmedByRoles is null
            ? null
            : JsonMapping.ToJsonDocument(update.ConfirmedByRoles);
        entity.TotalItemCount = update.TotalItemCount;
        entity.EligibleItemCount = update.EligibleItemCount;
        entity.IneligibleItemCount = update.IneligibleItemCount;
        entity.BlockedItemCount = await dbContext.WorkflowInstanceVersionChangeBatchItems
            .AsNoTracking()
            .CountAsync(
                item => item.BatchId == update.Id
                    && item.Status == InstanceVersionChangeBatchItemStatuses.Ineligible
                    && (item.ErrorCode == null
                        || (item.ErrorCode != "stale_since_selection"
                            && item.ErrorCode != "stale_since_preparation"
                            && item.ErrorCode != "stale")),
                cancellationToken);
        entity.WarningItemCount = update.WarningItemCount;
        entity.StaleItemCount = update.StaleItemCount;
        entity.QueuedItemCount = update.QueuedItemCount;
        entity.SucceededItemCount = update.SucceededItemCount;
        entity.SkippedItemCount = update.SkippedItemCount;
        entity.FailedItemCount = update.FailedItemCount;
        entity.CancelledItemCount = update.CancelledItemCount;
        entity.IssuesJson = CloneDocument(update.Issues);
        entity.PreparationJobId = update.PreparationJobId;
        entity.ExecutionJobId = update.ExecutionJobId;
        entity.CancelledBy = update.CancelledBy;
        entity.CancellationReason = update.CancellationReason;
        entity.UpdatedAt = update.UpdatedAt;
        entity.PreparedAt = update.PreparedAt;
        entity.ConfirmedAt = update.ConfirmedAt;
        entity.StartedAt = update.StartedAt;
        entity.CompletedAt = update.CompletedAt;
        entity.CancelledAt = update.CancelledAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<InstanceVersionChangeBatchItemRecord> UpdateItemAsync(
        InstanceVersionChangeBatchItemUpdateRecord update,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.WorkflowInstanceVersionChangeBatchItems.Local
            .SingleOrDefault(item => item.Id == update.Id)
            ?? await dbContext.WorkflowInstanceVersionChangeBatchItems
                .SingleAsync(item => item.Id == update.Id, cancellationToken);
        entity.Status = update.Status;
        entity.BlockersJson = CloneNonEmptyArrayDocument(update.Blockers);
        entity.WarningsJson = CloneNonEmptyArrayDocument(update.Warnings);
        entity.ResultJson = CloneDocument(update.Result);
        entity.ErrorCode = update.ErrorCode;
        entity.ErrorDescription = update.ErrorDescription;
        entity.UpdatedAt = update.UpdatedAt;
        entity.PreparedAt = update.PreparedAt;
        entity.StartedAt = update.StartedAt;
        entity.CompletedAt = update.CompletedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        var auditId = await GetAuditIdAsync(entity.Id, cancellationToken);
        var businessKey = await GetBusinessKeyAsync(entity.InstanceId, cancellationToken);
        return ToRecord(entity, businessKey, auditId);
    }

    public Task<int> CancelUnstartedItemsAsync(
        long batchId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken) =>
        dbContext.WorkflowInstanceVersionChangeBatchItems
            .Where(item => item.BatchId == batchId
                && (item.Status == InstanceVersionChangeBatchItemStatuses.Preparing
                    || item.Status == InstanceVersionChangeBatchItemStatuses.Eligible
                    || (item.Status == InstanceVersionChangeBatchItemStatuses.Queued
                        && item.StartedAt == null)))
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, InstanceVersionChangeBatchItemStatuses.Cancelled)
                    .SetProperty(item => item.UpdatedAt, cancelledAt)
                    .SetProperty(item => item.CompletedAt, cancelledAt),
                cancellationToken);

    private async Task<long?> GetAuditIdAsync(
        long batchItemId,
        CancellationToken cancellationToken) =>
        await dbContext.WorkflowInstanceVersionChanges
            .AsNoTracking()
            .Where(change => change.BatchItemId == batchItemId)
            .Select(change => (long?)change.Id)
            .SingleOrDefaultAsync(cancellationToken);

    private Task<string?> GetBusinessKeyAsync(
        long instanceId,
        CancellationToken cancellationToken) =>
        dbContext.WorkflowInstances
            .AsNoTracking()
            .Where(instance => instance.Id == instanceId)
            .Select(instance => instance.BusinessKey)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<long, long>> GetAuditIdsAsync(
        IReadOnlyCollection<WorkflowInstanceVersionChangeBatchItemEntity> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new Dictionary<long, long>();
        }

        var itemIds = items.Select(item => item.Id).ToArray();
        return await dbContext.WorkflowInstanceVersionChanges
            .AsNoTracking()
            .Where(change => change.BatchItemId != null && itemIds.Contains(change.BatchItemId.Value))
            .ToDictionaryAsync(
                change => change.BatchItemId!.Value,
                change => change.Id,
                cancellationToken);
    }

    private async Task<IReadOnlyDictionary<long, string?>> GetBusinessKeysAsync(
        IReadOnlyCollection<WorkflowInstanceVersionChangeBatchItemEntity> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new Dictionary<long, string?>();
        }

        var instanceIds = items.Select(item => item.InstanceId).Distinct().ToArray();
        return await dbContext.WorkflowInstances
            .AsNoTracking()
            .Where(instance => instanceIds.Contains(instance.Id))
            .ToDictionaryAsync(
                instance => instance.Id,
                instance => instance.BusinessKey,
                cancellationToken);
    }

    private static InstanceVersionChangeBatchRecord ToRecord(
        WorkflowInstanceVersionChangeBatchEntity entity) =>
        new(
            entity.Id,
            entity.WorkflowKey,
            entity.SourceWorkflowDefinitionId,
            entity.TargetWorkflowDefinitionId,
            entity.Reason,
            entity.SelectionJson.RootElement.Clone(),
            entity.Status,
            entity.PreparedBy,
            JsonMapping.ToStringList(entity.PreparedByRolesJson) ?? [],
            entity.ConfirmedBy,
            JsonMapping.ToStringList(entity.ConfirmedByRolesJson),
            entity.TotalItemCount,
            entity.EligibleItemCount,
            entity.IneligibleItemCount,
            entity.BlockedItemCount,
            entity.WarningItemCount,
            entity.StaleItemCount,
            entity.QueuedItemCount,
            entity.SucceededItemCount,
            entity.SkippedItemCount,
            entity.FailedItemCount,
            entity.CancelledItemCount,
            CloneElement(entity.IssuesJson),
            entity.PreparationJobId,
            entity.ExecutionJobId,
            entity.IdempotencyKey,
            entity.CancelledBy,
            entity.CancellationReason,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.PreparedAt,
            entity.ConfirmedAt,
            entity.StartedAt,
            entity.CompletedAt,
            entity.CancelledAt);

    private static InstanceVersionChangeBatchItemRecord ToRecord(
        WorkflowInstanceVersionChangeBatchItemEntity entity,
        string? businessKey,
        long? auditId) =>
        new(
            entity.Id,
            entity.BatchId,
            entity.InstanceId,
            businessKey,
            entity.CapturedSourceWorkflowDefinitionId,
            entity.CapturedInstanceUpdatedAt,
            entity.Status,
            CloneElement(entity.BlockersJson),
            CloneElement(entity.WarningsJson),
            CloneElement(entity.ResultJson),
            entity.ErrorCode,
            entity.ErrorDescription,
            auditId,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.PreparedAt,
            entity.StartedAt,
            entity.CompletedAt);

    private static JsonDocument? CloneDocument(JsonElement? value) =>
        value is null ? null : JsonDocument.Parse(value.Value.GetRawText());

    private static JsonDocument? CloneNonEmptyArrayDocument(JsonElement? value) =>
        value is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0
            ? null
            : JsonDocument.Parse(array.GetRawText());

    private static JsonElement? CloneElement(JsonDocument? document) =>
        document?.RootElement.Clone();
}
