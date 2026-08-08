using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IAdministrativeActionBatchRepository
{
    Task<AdministrativeActionBatchRecord> AddAsync(
        NewAdministrativeActionBatchRecord batch,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AdministrativeActionBatchItemRecord>> AddItemsAsync(
        long batchId,
        IReadOnlyCollection<NewAdministrativeActionBatchItemRecord> items,
        CancellationToken cancellationToken);

    Task<AdministrativeActionBatchRecord?> GetAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<AdministrativeActionBatchRecord?> FindByIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task LockIdempotencyKeyAsync(
        string preparedBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<PagedResult<AdministrativeActionBatchRecord>> ListAsync(
        AdministrativeActionBatchSearch search,
        CancellationToken cancellationToken);

    Task<AdministrativeActionBatchItemRecord?> GetItemAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<PagedResult<AdministrativeActionBatchItemRecord>> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AdministrativeActionBatchItemRecord>> ListItemsForProcessingAsync(
        long batchId,
        IReadOnlyCollection<string> statuses,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, int>> CountItemsByStatusAsync(
        long batchId,
        CancellationToken cancellationToken);

    Task<int> SumAffectedTaskCountAsync(
        long batchId,
        IReadOnlyCollection<string>? statuses,
        CancellationToken cancellationToken);

    Task<int> TransitionItemsAsync(
        long batchId,
        IReadOnlyCollection<string> fromStatuses,
        string toStatus,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task<AdministrativeActionBatchRecord> UpdateAsync(
        AdministrativeActionBatchUpdateRecord update,
        CancellationToken cancellationToken);

    Task<AdministrativeActionBatchItemRecord> UpdateItemAsync(
        AdministrativeActionBatchItemUpdateRecord update,
        CancellationToken cancellationToken);

    Task<int> CancelUnstartedItemsAsync(
        long batchId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken);
}
