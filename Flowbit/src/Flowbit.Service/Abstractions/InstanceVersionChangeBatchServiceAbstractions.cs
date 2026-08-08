using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IInstanceVersionChangeBatchService
{
    Task<PagedResult<InstanceVersionChangeCandidateDto>> SearchCandidatesAsync(
        InstanceVersionChangeCandidateSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceVersionChangeBatchDetailDto> CreateAsync(
        CreateInstanceVersionChangeBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceVersionChangeBatchSummaryDto>> ListAsync(
        InstanceVersionChangeBatchSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceVersionChangeBatchDetailDto?> GetAsync(
        long batchId,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceVersionChangeBatchItemDto>?> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceVersionChangeBatchDetailDto?> ConfirmAsync(
        long batchId,
        ConfirmInstanceVersionChangeBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceVersionChangeBatchDetailDto?> CancelAsync(
        long batchId,
        CancelInstanceVersionChangeBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);
}

public interface IInstanceVersionChangeBatchJobProcessor
{
    Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken);
}

public interface IInstanceVersionChangeBatchExecutor
{
    Task<InstanceVersionChangeBatchExecutionOutcome>
        ExecuteInstanceVersionChangeBatchItemAsync(
            InstanceVersionChangeBatchExecutionRequest request,
            ActorContext actor,
            CancellationToken cancellationToken);
}
