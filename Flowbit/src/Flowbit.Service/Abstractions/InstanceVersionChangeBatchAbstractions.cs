using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IInstanceVersionChangeBatchRepository
{
    Task<InstanceVersionChangeBatchRecord> AddAsync(
        NewInstanceVersionChangeBatchRecord batch,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceVersionChangeBatchItemRecord>> AddItemsAsync(
        long batchId,
        IReadOnlyCollection<NewInstanceVersionChangeBatchItemRecord> items,
        CancellationToken cancellationToken);

    Task<InstanceVersionChangeBatchRecord?> GetAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<InstanceVersionChangeBatchRecord?> FindByIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task LockIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceVersionChangeBatchRecord>> ListAsync(
        InstanceVersionChangeBatchSearch search,
        CancellationToken cancellationToken);

    Task<InstanceVersionChangeBatchItemRecord?> GetItemAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceVersionChangeBatchItemRecord>> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceVersionChangeBatchItemRecord>> ListItemsForProcessingAsync(
        long batchId,
        IReadOnlyCollection<string> statuses,
        long? afterItemId,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, int>> CountItemsByStatusAsync(
        long batchId,
        CancellationToken cancellationToken);

    Task<int> CountItemsWithWarningsAsync(
        long batchId,
        CancellationToken cancellationToken);

    Task<int> CountStaleItemsAsync(
        long batchId,
        CancellationToken cancellationToken);

    Task<int> TransitionItemsAsync(
        long batchId,
        IReadOnlyCollection<string> fromStatuses,
        string toStatus,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task<int> FailItemsAsync(
        long batchId,
        IReadOnlyCollection<string> fromStatuses,
        string errorCode,
        string errorDescription,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task<InstanceVersionChangeBatchRecord> UpdateAsync(
        InstanceVersionChangeBatchUpdateRecord update,
        CancellationToken cancellationToken);

    Task<InstanceVersionChangeBatchItemRecord> UpdateItemAsync(
        InstanceVersionChangeBatchItemUpdateRecord update,
        CancellationToken cancellationToken);

    Task<int> CancelUnstartedItemsAsync(
        long batchId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken);
}
