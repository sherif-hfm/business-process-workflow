using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IInstanceVariableUpdateRepository
{
    Task<InstanceVariableUpdateAuditRecord?> GetAsync(
        long operationId,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateAuditRecord?> FindByIdempotencyKeyAsync(
        long instanceId,
        string performedBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task LockIdempotencyKeyAsync(
        long instanceId,
        string performedBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateAuditRecord> AddAsync(
        NewInstanceVariableUpdateAuditRecord create,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateAuditRecord> SetResultAsync(
        long operationId,
        JsonElement result,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceVariableUpdateVariableRecord>>
        ListVariablesAsync(
            long operationId,
            CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceVariableUpdateAuditRecord>> ListByInstanceAsync(
        long instanceId,
        CancellationToken cancellationToken);
}

public interface IInstanceVariableUpdateCandidateRepository
{
    Task<PagedResult<InstanceListItem>> SearchAsync(
        InstanceVariableUpdateCandidateQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FrozenInstanceVariableUpdateCandidate>> MaterializeAsync(
        InstanceVariableUpdateCandidateQuery query,
        IReadOnlyCollection<long> excludedInstanceIds,
        int limit,
        CancellationToken cancellationToken);
}

public interface IInstanceVariableUpdateBatchRepository
{
    Task<InstanceVariableUpdateBatchRecord?> GetAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateBatchRecord?> FindByIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task LockIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateBatchRecord> AddAsync(
        NewInstanceVariableUpdateBatchRecord create,
        CancellationToken cancellationToken);

    Task AddItemsAsync(
        long batchId,
        IReadOnlyCollection<NewInstanceVariableUpdateBatchItemRecord> items,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateBatchRecord> UpdateAsync(
        InstanceVariableUpdateBatchUpdateRecord update,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceVariableUpdateBatchRecord>> ListAsync(
        InstanceVariableUpdateBatchSearch search,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateBatchItemRecord?> GetItemAsync(
        long itemId,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceVariableUpdateBatchItemRecord>> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceVariableUpdateBatchItemRecord>>
        ListItemsForProcessingAsync(
            long batchId,
            long workflowDefinitionId,
            IReadOnlyCollection<string> statuses,
            long? afterItemId,
            int take,
            CancellationToken cancellationToken);

    Task<InstanceVariableUpdateBatchItemRecord> UpdateItemAsync(
        InstanceVariableUpdateBatchItemUpdateRecord update,
        CancellationToken cancellationToken);

    Task<int> TransitionItemsAsync(
        long batchId,
        IReadOnlyCollection<string> fromStatuses,
        string toStatus,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);

    Task<int> CancelUnstartedItemsAsync(
        long batchId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken);

    Task<int> FailItemsAsync(
        long batchId,
        long workflowDefinitionId,
        IReadOnlyCollection<string> fromStatuses,
        string errorCode,
        string errorDescription,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, int>> CountItemsByStatusAsync(
        long batchId,
        CancellationToken cancellationToken);

    Task<int> CountItemsWithWarningsAsync(
        long batchId,
        CancellationToken cancellationToken);

    Task AddJobLinkAsync(
        NewInstanceVariableUpdateBatchJobLinkRecord create,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceVariableUpdateBatchJobLinkRecord>> ListJobLinksAsync(
        long batchId,
        CancellationToken cancellationToken);
}

public interface IInstanceVariableUpdateBatchService
{
    Task<PagedResult<InstanceVariableUpdateCandidateDto>> SearchCandidatesAsync(
        InstanceVariableUpdateCandidateSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateBatchDetailDto> CreateAsync(
        CreateInstanceVariableUpdateBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceVariableUpdateBatchSummaryDto>> ListAsync(
        InstanceVariableUpdateBatchSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateBatchDetailDto?> GetAsync(
        long batchId,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceVariableUpdateBatchItemDto>?> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateBatchDetailDto?> ConfirmAsync(
        long batchId,
        ConfirmInstanceVariableUpdateBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceVariableUpdateBatchDetailDto?> CancelAsync(
        long batchId,
        CancelInstanceVariableUpdateBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);
}

public interface IInstanceVariableUpdateBatchJobProcessor
{
    Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken);
}
