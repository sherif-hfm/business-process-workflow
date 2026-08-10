using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

public sealed class InstanceVariableUpdateBatchRepository(AppDbContext dbContext)
    : IInstanceVariableUpdateBatchRepository
{
    public async Task<InstanceVariableUpdateBatchRecord?> GetAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var entity = forUpdate
            ? await dbContext.InstanceVariableUpdateBatches
                .FromSqlInterpolated(
                    $"SELECT * FROM flowbit.instance_variable_update_batches WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.InstanceVariableUpdateBatches
                .AsNoTracking()
                .SingleOrDefaultAsync(batch => batch.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<InstanceVariableUpdateBatchRecord?> FindByIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.InstanceVariableUpdateBatches
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
            $"SELECT pg_advisory_xact_lock(hashtext('instance-variable-update-batch'), hashtext({lockKey}))",
            cancellationToken);
    }

    public async Task<InstanceVariableUpdateBatchRecord> AddAsync(
        NewInstanceVariableUpdateBatchRecord create,
        CancellationToken cancellationToken)
    {
        var entity = new InstanceVariableUpdateBatchEntity
        {
            WorkflowKey = create.WorkflowKey,
            VariablesJson = JsonMapping.ToJsonDocument(create.Variables),
            SelectionJson = JsonMapping.ToJsonDocument(create.Selection),
            Reason = create.Reason,
            Status = InstanceVariableUpdateBatchStatuses.Preparing,
            PreparedBy = create.PreparedBy,
            PreparedByRolesJson = JsonMapping.ToJsonDocument(create.PreparedByRoles),
            IdempotencyKey = create.IdempotencyKey,
            CreatedAt = create.CreatedAt,
            UpdatedAt = create.CreatedAt
        };
        dbContext.InstanceVariableUpdateBatches.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task AddItemsAsync(
        long batchId,
        IReadOnlyCollection<NewInstanceVariableUpdateBatchItemRecord> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        dbContext.InstanceVariableUpdateBatchItems.AddRange(items.Select(item =>
            new InstanceVariableUpdateBatchItemEntity
            {
                BatchId = batchId,
                InstanceId = item.InstanceId,
                CapturedWorkflowDefinitionId = item.CapturedWorkflowDefinitionId,
                CapturedInstanceUpdatedAt = item.CapturedInstanceUpdatedAt,
                Status = InstanceVariableUpdateBatchItemStatuses.Preparing,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.CreatedAt
            }));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<InstanceVariableUpdateBatchRecord> UpdateAsync(
        InstanceVariableUpdateBatchUpdateRecord update,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.InstanceVariableUpdateBatches.Local
            .SingleOrDefault(batch => batch.Id == update.Id)
            ?? await dbContext.InstanceVariableUpdateBatches
                .SingleAsync(batch => batch.Id == update.Id, cancellationToken);
        entity.Status = update.Status;
        entity.ConfirmedBy = update.ConfirmedBy;
        entity.ConfirmedByRolesJson = update.ConfirmedByRoles is null
            ? null
            : JsonMapping.ToJsonDocument(update.ConfirmedByRoles);
        entity.TotalItemCount = update.TotalItemCount;
        entity.EligibleItemCount = update.EligibleItemCount;
        entity.IneligibleItemCount = update.IneligibleItemCount;
        entity.WarningItemCount = update.WarningItemCount;
        entity.QueuedItemCount = update.QueuedItemCount;
        entity.SucceededItemCount = update.SucceededItemCount;
        entity.SkippedItemCount = update.SkippedItemCount;
        entity.FailedItemCount = update.FailedItemCount;
        entity.CancelledItemCount = update.CancelledItemCount;
        entity.IssuesJson = CloneDocument(update.Issues);
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

    public async Task<PagedResult<InstanceVariableUpdateBatchRecord>> ListAsync(
        InstanceVariableUpdateBatchSearch search,
        CancellationToken cancellationToken)
    {
        IQueryable<InstanceVariableUpdateBatchEntity> query =
            dbContext.InstanceVariableUpdateBatches.AsNoTracking();
        if (search.WorkflowKey is not null)
        {
            query = query.Where(batch => batch.WorkflowKey == search.WorkflowKey);
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
        return new PagedResult<InstanceVariableUpdateBatchRecord>(
            entities.Select(ToRecord).ToArray(),
            search.Page,
            search.PageSize,
            totalCount);
    }

    public async Task<InstanceVariableUpdateBatchItemRecord?> GetItemAsync(
        long itemId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var entity = forUpdate
            ? await dbContext.InstanceVariableUpdateBatchItems
                .FromSqlInterpolated(
                    $"SELECT * FROM flowbit.instance_variable_update_batch_items WHERE \"Id\" = {itemId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.InstanceVariableUpdateBatchItems
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == itemId, cancellationToken);
        if (entity is null)
        {
            return null;
        }
        return ToRecord(
            entity,
            await GetBusinessKeyAsync(entity.InstanceId, cancellationToken),
            await GetUpdateOperationIdAsync(entity.Id, cancellationToken));
    }

    public async Task<PagedResult<InstanceVariableUpdateBatchItemRecord>> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.InstanceVariableUpdateBatchItems
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
        var businessKeys = await GetBusinessKeysAsync(entities, cancellationToken);
        var updateOperationIds = await GetUpdateOperationIdsAsync(entities, cancellationToken);
        return new PagedResult<InstanceVariableUpdateBatchItemRecord>(
            entities.Select(entity => ToRecord(
                entity,
                businessKeys.GetValueOrDefault(entity.InstanceId),
                updateOperationIds.TryGetValue(entity.Id, out var updateOperationId)
                    ? updateOperationId
                    : null)).ToArray(),
            page,
            pageSize,
            totalCount);
    }

    public async Task<IReadOnlyList<InstanceVariableUpdateBatchItemRecord>>
        ListItemsForProcessingAsync(
            long batchId,
            long workflowDefinitionId,
            IReadOnlyCollection<string> statuses,
            long? afterItemId,
            int take,
            CancellationToken cancellationToken)
    {
        if (statuses.Count == 0 || take <= 0)
        {
            return [];
        }

        var query = dbContext.InstanceVariableUpdateBatchItems
            .AsNoTracking()
            .Where(item => item.BatchId == batchId
                && item.CapturedWorkflowDefinitionId == workflowDefinitionId
                && statuses.Contains(item.Status));
        if (afterItemId is long cursor)
        {
            query = query.Where(item => item.Id > cursor);
        }
        var entities = await query
            .OrderBy(item => item.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        var businessKeys = await GetBusinessKeysAsync(entities, cancellationToken);
        var updateOperationIds = await GetUpdateOperationIdsAsync(entities, cancellationToken);
        return entities.Select(entity => ToRecord(
            entity,
            businessKeys.GetValueOrDefault(entity.InstanceId),
            updateOperationIds.TryGetValue(entity.Id, out var updateOperationId)
                ? updateOperationId
                : null)).ToArray();
    }

    public async Task<InstanceVariableUpdateBatchItemRecord> UpdateItemAsync(
        InstanceVariableUpdateBatchItemUpdateRecord update,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.InstanceVariableUpdateBatchItems.Local
            .SingleOrDefault(item => item.Id == update.Id)
            ?? await dbContext.InstanceVariableUpdateBatchItems
                .SingleAsync(item => item.Id == update.Id, cancellationToken);
        if (update.UpdateOperationId is long operationId
            && !await dbContext.InstanceVariableUpdates.AnyAsync(
                audit => audit.Id == operationId
                    && audit.BatchItemId == entity.Id
                    && audit.BatchId == entity.BatchId
                    && audit.InstanceId == entity.InstanceId,
                cancellationToken))
        {
            throw new InvalidOperationException(
                $"Variable-update operation #{operationId} does not own batch item #{entity.Id}.");
        }
        entity.Status = update.Status;
        entity.PlanJson = CloneNonEmptyArrayDocument(update.Plan);
        entity.WarningsJson = CloneNonEmptyArrayDocument(update.Warnings);
        entity.ResultJson = CloneDocument(update.Result);
        entity.ErrorCode = update.ErrorCode;
        entity.ErrorDescription = update.ErrorDescription;
        entity.UpdatedAt = update.UpdatedAt;
        entity.PreparedAt = update.PreparedAt;
        entity.StartedAt = update.StartedAt;
        entity.CompletedAt = update.CompletedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(
            entity,
            await GetBusinessKeyAsync(entity.InstanceId, cancellationToken),
            await GetUpdateOperationIdAsync(entity.Id, cancellationToken));
    }

    public Task<int> TransitionItemsAsync(
        long batchId,
        IReadOnlyCollection<string> fromStatuses,
        string toStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        if (fromStatuses.Count == 0)
        {
            return Task.FromResult(0);
        }

        var query = dbContext.InstanceVariableUpdateBatchItems
            .Where(item => item.BatchId == batchId && fromStatuses.Contains(item.Status));
        if (toStatus is InstanceVariableUpdateBatchItemStatuses.Failed
            or InstanceVariableUpdateBatchItemStatuses.Skipped
            or InstanceVariableUpdateBatchItemStatuses.Cancelled)
        {
            return query.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, toStatus)
                    .SetProperty(item => item.UpdatedAt, updatedAt)
                    .SetProperty(item => item.CompletedAt, updatedAt),
                cancellationToken);
        }
        return query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, toStatus)
                .SetProperty(item => item.UpdatedAt, updatedAt),
            cancellationToken);
    }

    public Task<int> CancelUnstartedItemsAsync(
        long batchId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken) =>
        dbContext.InstanceVariableUpdateBatchItems
            .Where(item => item.BatchId == batchId
                && (item.Status == InstanceVariableUpdateBatchItemStatuses.Preparing
                    || item.Status == InstanceVariableUpdateBatchItemStatuses.Eligible
                    || (item.Status == InstanceVariableUpdateBatchItemStatuses.Queued
                        && item.StartedAt == null)))
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, InstanceVariableUpdateBatchItemStatuses.Cancelled)
                    .SetProperty(item => item.UpdatedAt, cancelledAt)
                    .SetProperty(item => item.CompletedAt, cancelledAt),
                cancellationToken);

    public Task<int> FailItemsAsync(
        long batchId,
        long workflowDefinitionId,
        IReadOnlyCollection<string> fromStatuses,
        string errorCode,
        string errorDescription,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        if (fromStatuses.Count == 0)
        {
            return Task.FromResult(0);
        }

        return dbContext.InstanceVariableUpdateBatchItems
            .Where(item => item.BatchId == batchId
                && item.CapturedWorkflowDefinitionId == workflowDefinitionId
                && fromStatuses.Contains(item.Status))
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, InstanceVariableUpdateBatchItemStatuses.Failed)
                    .SetProperty(item => item.ErrorCode, errorCode)
                    .SetProperty(item => item.ErrorDescription, errorDescription)
                    .SetProperty(item => item.UpdatedAt, failedAt)
                    .SetProperty(item => item.CompletedAt, failedAt),
                cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountItemsByStatusAsync(
        long batchId,
        CancellationToken cancellationToken) =>
        await dbContext.InstanceVariableUpdateBatchItems
            .AsNoTracking()
            .Where(item => item.BatchId == batchId)
            .GroupBy(item => item.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

    public Task<int> CountItemsWithWarningsAsync(
        long batchId,
        CancellationToken cancellationToken) =>
        dbContext.InstanceVariableUpdateBatchItems
            .AsNoTracking()
            .CountAsync(
                item => item.BatchId == batchId && item.WarningsJson != null,
                cancellationToken);

    public async Task AddJobLinkAsync(
        NewInstanceVariableUpdateBatchJobLinkRecord create,
        CancellationToken cancellationToken)
    {
        dbContext.InstanceVariableUpdateBatchJobLinks.Add(
            new InstanceVariableUpdateBatchJobLinkEntity
            {
                BatchId = create.BatchId,
                WorkflowDefinitionId = create.WorkflowDefinitionId,
                Phase = create.Phase,
                OriginalJobId = create.OriginalJobId,
                JobId = create.JobId
            });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InstanceVariableUpdateBatchJobLinkRecord>> ListJobLinksAsync(
        long batchId,
        CancellationToken cancellationToken) =>
        await dbContext.InstanceVariableUpdateBatchJobLinks
            .AsNoTracking()
            .Where(link => link.BatchId == batchId)
            .OrderBy(link => link.Phase)
            .ThenBy(link => link.WorkflowDefinitionId)
            .Select(link => new InstanceVariableUpdateBatchJobLinkRecord(
                link.Id,
                link.BatchId,
                link.WorkflowDefinitionId,
                link.Phase,
                link.OriginalJobId,
                link.JobId))
            .ToListAsync(cancellationToken);

    private Task<string?> GetBusinessKeyAsync(
        long instanceId,
        CancellationToken cancellationToken) =>
        dbContext.WorkflowInstances
            .AsNoTracking()
            .Where(instance => instance.Id == instanceId)
            .Select(instance => instance.BusinessKey)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<long, string?>> GetBusinessKeysAsync(
        IReadOnlyCollection<InstanceVariableUpdateBatchItemEntity> items,
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

    private Task<long?> GetUpdateOperationIdAsync(
        long itemId,
        CancellationToken cancellationToken) =>
        dbContext.InstanceVariableUpdates
            .AsNoTracking()
            .Where(audit => audit.BatchItemId == itemId)
            .Select(audit => (long?)audit.Id)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<long, long>> GetUpdateOperationIdsAsync(
        IReadOnlyCollection<InstanceVariableUpdateBatchItemEntity> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new Dictionary<long, long>();
        }

        var itemIds = items.Select(item => item.Id).ToArray();
        return await dbContext.InstanceVariableUpdates
            .AsNoTracking()
            .Where(audit => audit.BatchItemId != null
                && itemIds.Contains(audit.BatchItemId.Value))
            .ToDictionaryAsync(
                audit => audit.BatchItemId!.Value,
                audit => audit.Id,
                cancellationToken);
    }

    private static InstanceVariableUpdateBatchRecord ToRecord(
        InstanceVariableUpdateBatchEntity entity) =>
        new(
            entity.Id,
            entity.WorkflowKey,
            entity.VariablesJson.RootElement.Clone(),
            entity.SelectionJson.RootElement.Clone(),
            entity.Reason,
            entity.Status,
            entity.PreparedBy,
            JsonMapping.ToStringList(entity.PreparedByRolesJson) ?? [],
            entity.ConfirmedBy,
            JsonMapping.ToStringList(entity.ConfirmedByRolesJson),
            entity.TotalItemCount,
            entity.EligibleItemCount,
            entity.IneligibleItemCount,
            entity.WarningItemCount,
            entity.QueuedItemCount,
            entity.SucceededItemCount,
            entity.SkippedItemCount,
            entity.FailedItemCount,
            entity.CancelledItemCount,
            CloneElement(entity.IssuesJson),
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

    private static InstanceVariableUpdateBatchItemRecord ToRecord(
        InstanceVariableUpdateBatchItemEntity entity,
        string? businessKey,
        long? updateOperationId) =>
        new(
            entity.Id,
            entity.BatchId,
            entity.InstanceId,
            businessKey,
            entity.CapturedWorkflowDefinitionId,
            entity.CapturedInstanceUpdatedAt,
            entity.Status,
            CloneElement(entity.PlanJson),
            CloneElement(entity.WarningsJson),
            CloneElement(entity.ResultJson),
            updateOperationId,
            entity.ErrorCode,
            entity.ErrorDescription,
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
