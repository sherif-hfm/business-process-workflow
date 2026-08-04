using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

public sealed class AdministrativeActionBatchRepository(AppDbContext dbContext)
    : IAdministrativeActionBatchRepository
{
    public async Task<AdministrativeActionBatchRecord> AddAsync(
        NewAdministrativeActionBatchRecord batch,
        CancellationToken cancellationToken)
    {
        var entity = new AdministrativeActionBatchEntity
        {
            TargetWorkflowDefinitionId = batch.TargetWorkflowDefinitionId,
            WorkflowKey = batch.WorkflowKey,
            FlowExternalId = batch.FlowExternalId,
            Reason = batch.Reason,
            CommonVariablesJson = ToDocument(batch.CommonVariables),
            SelectionJson = JsonMapping.ToJsonDocument(batch.Selection),
            Status = AdministrativeActionBatchStatuses.Preparing,
            PreparedBy = batch.PreparedBy,
            PreparedByRolesJson = JsonMapping.ToJsonDocument(batch.PreparedByRoles),
            IdempotencyKey = batch.IdempotencyKey,
            CreatedAt = batch.CreatedAt,
            UpdatedAt = batch.CreatedAt
        };
        dbContext.AdministrativeActionBatches.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<AdministrativeActionBatchItemRecord>> AddItemsAsync(
        long batchId,
        IReadOnlyCollection<NewAdministrativeActionBatchItemRecord> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var entities = items.Select(item => new AdministrativeActionBatchItemEntity
        {
            BatchId = batchId,
            InstanceId = item.InstanceId,
            UserTaskId = item.UserTaskId,
            TokenId = item.TokenId,
            SourceWorkflowDefinitionId = item.SourceWorkflowDefinitionId,
            TargetWorkflowDefinitionId = item.TargetWorkflowDefinitionId,
            CapturedInstanceUpdatedAt = item.CapturedInstanceUpdatedAt,
            CapturedUserTaskUpdatedAt = item.CapturedUserTaskUpdatedAt,
            Status = AdministrativeActionBatchItemStatuses.Preparing,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.CreatedAt
        }).ToArray();
        dbContext.AdministrativeActionBatchItems.AddRange(entities);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entities.Select(ToRecord).ToArray();
    }

    public async Task<AdministrativeActionBatchRecord?> GetAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var entity = forUpdate
            ? await dbContext.AdministrativeActionBatches
                .FromSqlInterpolated(
                    $"SELECT * FROM flowbit.administrative_action_batches WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.AdministrativeActionBatches
                .AsNoTracking()
                .SingleOrDefaultAsync(batch => batch.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<AdministrativeActionBatchRecord?> FindByIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.AdministrativeActionBatches
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
            $"SELECT pg_advisory_xact_lock(hashtext('administrative-action-batch'), hashtext({lockKey}))",
            cancellationToken);
    }

    public async Task<PagedResult<AdministrativeActionBatchRecord>> ListAsync(
        AdministrativeActionBatchSearch search,
        AdministrativeActionBatchListAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var lowerActorRoles = authorization.LowerActorRoles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IQueryable<AdministrativeActionBatchEntity> query = lowerActorRoles.Length == 0
            ? dbContext.AdministrativeActionBatches
                .AsNoTracking()
                .Where(_ => false)
            : dbContext.AdministrativeActionBatches
                .FromSqlInterpolated($"""
                    SELECT batch.*
                    FROM flowbit.administrative_action_batches AS batch
                    INNER JOIN flowbit.workflow_definitions AS definition
                        ON definition."Id" = batch."TargetWorkflowDefinitionId"
                       AND definition."WorkflowKey" = batch."WorkflowKey"
                    WHERE EXISTS
                      (
                          SELECT 1
                          FROM jsonb_array_elements(
                              COALESCE(
                                  definition."Definition" -> 'sequenceFlows',
                                  '[]'::jsonb)) AS flow(value)
                          WHERE lower(btrim(flow.value ->> 'externalId')) =
                                lower(btrim(batch."FlowExternalId"))
                            AND COALESCE(
                                    (flow.value ->> 'isAdministrative')::boolean,
                                    false)
                            AND COALESCE(
                                    (flow.value ->> 'isBatchable')::boolean,
                                    false)
                            AND EXISTS
                            (
                                SELECT 1
                                FROM jsonb_array_elements_text(
                                    COALESCE(
                                        flow.value -> 'roles',
                                        '[]'::jsonb)) AS flow_role(value)
                                WHERE lower(btrim(flow_role.value)) = ANY ({lowerActorRoles})
                            )
                      )
                    """)
                .AsNoTracking();
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
        return new PagedResult<AdministrativeActionBatchRecord>(
            entities.Select(ToRecord).ToArray(),
            search.Page,
            search.PageSize,
            totalCount);
    }

    public async Task<AdministrativeActionBatchItemRecord?> GetItemAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var entity = forUpdate
            ? await dbContext.AdministrativeActionBatchItems
                .FromSqlInterpolated(
                    $"SELECT * FROM flowbit.administrative_action_batch_items WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.AdministrativeActionBatchItems
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<PagedResult<AdministrativeActionBatchItemRecord>> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.AdministrativeActionBatchItems
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
        return new PagedResult<AdministrativeActionBatchItemRecord>(
            entities.Select(ToRecord).ToArray(),
            page,
            pageSize,
            totalCount);
    }

    public async Task<IReadOnlyList<AdministrativeActionBatchItemRecord>> ListItemsForProcessingAsync(
        long batchId,
        IReadOnlyCollection<string> statuses,
        int limit,
        CancellationToken cancellationToken)
    {
        if (statuses.Count == 0 || limit <= 0)
        {
            return [];
        }

        var entities = await dbContext.AdministrativeActionBatchItems
            .AsNoTracking()
            .Where(item => item.BatchId == batchId && statuses.Contains(item.Status))
            .OrderBy(item => item.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, int>> CountItemsByStatusAsync(
        long batchId,
        CancellationToken cancellationToken) =>
        await dbContext.AdministrativeActionBatchItems
            .AsNoTracking()
            .Where(item => item.BatchId == batchId)
            .GroupBy(item => item.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

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

        var query = dbContext.AdministrativeActionBatchItems
            .Where(item => item.BatchId == batchId && fromStatuses.Contains(item.Status))
            ;
        if (toStatus is AdministrativeActionBatchItemStatuses.Failed
            or AdministrativeActionBatchItemStatuses.Skipped
            or AdministrativeActionBatchItemStatuses.Cancelled)
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

    public async Task<AdministrativeActionBatchRecord> UpdateAsync(
        AdministrativeActionBatchUpdateRecord update,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.AdministrativeActionBatches.Local
            .SingleOrDefault(batch => batch.Id == update.Id)
            ?? await dbContext.AdministrativeActionBatches
                .SingleAsync(batch => batch.Id == update.Id, cancellationToken);
        entity.Status = update.Status;
        entity.ConfirmedBy = update.ConfirmedBy;
        entity.ConfirmedByRolesJson = update.ConfirmedByRoles is null
            ? null
            : JsonMapping.ToJsonDocument(update.ConfirmedByRoles);
        entity.TotalItemCount = update.TotalItemCount;
        entity.EligibleItemCount = update.EligibleItemCount;
        entity.IneligibleItemCount = update.IneligibleItemCount;
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

    public async Task<AdministrativeActionBatchItemRecord> UpdateItemAsync(
        AdministrativeActionBatchItemUpdateRecord update,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.AdministrativeActionBatchItems.Local
            .SingleOrDefault(item => item.Id == update.Id)
            ?? await dbContext.AdministrativeActionBatchItems
                .SingleAsync(item => item.Id == update.Id, cancellationToken);
        entity.Status = update.Status;
        entity.IssuesJson = CloneDocument(update.Issues);
        entity.ResultJson = CloneDocument(update.Result);
        entity.ErrorCode = update.ErrorCode;
        entity.ErrorDescription = update.ErrorDescription;
        entity.NewUserTaskId = update.NewUserTaskId;
        entity.VersionChangeAuditId = update.VersionChangeAuditId;
        entity.UpdatedAt = update.UpdatedAt;
        entity.PreparedAt = update.PreparedAt;
        entity.StartedAt = update.StartedAt;
        entity.CompletedAt = update.CompletedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public Task<int> CancelUnstartedItemsAsync(
        long batchId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken) =>
        dbContext.AdministrativeActionBatchItems
            .Where(item => item.BatchId == batchId
                && (item.Status == AdministrativeActionBatchItemStatuses.Preparing
                    || item.Status == AdministrativeActionBatchItemStatuses.Eligible
                    || (item.Status == AdministrativeActionBatchItemStatuses.Queued
                        && item.StartedAt == null)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AdministrativeActionBatchItemStatuses.Cancelled)
                .SetProperty(item => item.UpdatedAt, cancelledAt)
                .SetProperty(item => item.CompletedAt, cancelledAt),
                cancellationToken);

    private static AdministrativeActionBatchRecord ToRecord(
        AdministrativeActionBatchEntity entity) =>
        new(
            entity.Id,
            entity.TargetWorkflowDefinitionId,
            entity.WorkflowKey,
            entity.FlowExternalId,
            entity.Reason,
            JsonMapping.ToDictionary(entity.CommonVariablesJson)
                ?? new Dictionary<string, JsonElement>(),
            entity.SelectionJson.RootElement.Clone(),
            entity.Status,
            entity.PreparedBy,
            JsonMapping.ToStringList(entity.PreparedByRolesJson) ?? [],
            entity.ConfirmedBy,
            JsonMapping.ToStringList(entity.ConfirmedByRolesJson),
            entity.TotalItemCount,
            entity.EligibleItemCount,
            entity.IneligibleItemCount,
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

    private static AdministrativeActionBatchItemRecord ToRecord(
        AdministrativeActionBatchItemEntity entity) =>
        new(
            entity.Id,
            entity.BatchId,
            entity.InstanceId,
            entity.UserTaskId,
            entity.TokenId,
            entity.SourceWorkflowDefinitionId,
            entity.TargetWorkflowDefinitionId,
            entity.CapturedInstanceUpdatedAt,
            entity.CapturedUserTaskUpdatedAt,
            entity.Status,
            CloneElement(entity.IssuesJson),
            CloneElement(entity.ResultJson),
            entity.ErrorCode,
            entity.ErrorDescription,
            entity.NewUserTaskId,
            entity.VersionChangeAuditId,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.PreparedAt,
            entity.StartedAt,
            entity.CompletedAt);

    private static JsonDocument ToDocument(
        IReadOnlyDictionary<string, JsonElement> values) =>
        JsonDocument.Parse(JsonSerializer.Serialize(values));

    private static JsonDocument? CloneDocument(JsonElement? value) =>
        value is null ? null : JsonDocument.Parse(value.Value.GetRawText());

    private static JsonElement? CloneElement(JsonDocument? document) =>
        document?.RootElement.Clone();
}
