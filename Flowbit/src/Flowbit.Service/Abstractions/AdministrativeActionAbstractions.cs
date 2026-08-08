using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IAdministrativeActionCandidateRepository
{
    Task<PagedResult<AdministrativeActionCandidateRecord>> SearchAsync(
        AdministrativeActionCandidateQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a server-side selection to an immutable candidate set. The
    /// implementation returns at most <paramref name="limit"/> + 1 rows so the
    /// caller can reject an over-limit batch without silently truncating it.
    /// </summary>
    Task<IReadOnlyList<AdministrativeActionCandidateRecord>> MaterializeAsync(
        AdministrativeActionCandidateQuery query,
        IReadOnlyCollection<AdministrativeActionPositionKey> excludedPositions,
        int limit,
        CancellationToken cancellationToken);
}

public interface IAdministrativeActionBatchService
{
    Task<IReadOnlyList<WorkflowSummaryDto>> ListWorkflowCatalogAsync(
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AdministrativeActionSourceNodeDto>> ListSourceNodesAsync(
        long workflowDefinitionId,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AdministrativeActionSummaryDto>> ListActionsAsync(
        long workflowDefinitionId,
        int sourceNodeId,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<AdministrativeActionCandidateDto>> SearchCandidatesAsync(
        AdministrativeActionCandidateSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<AdministrativeActionBatchDetailDto> CreateAsync(
        CreateAdministrativeActionBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<AdministrativeActionBatchSummaryDto>> ListAsync(
        AdministrativeActionBatchSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<AdministrativeActionBatchDetailDto?> GetAsync(
        long batchId,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<AdministrativeActionBatchItemDto>?> ListItemsAsync(
        long batchId,
        string? status,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<AdministrativeActionBatchDetailDto?> ConfirmAsync(
        long batchId,
        ConfirmAdministrativeActionBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<AdministrativeActionBatchDetailDto?> CancelAsync(
        long batchId,
        CancelAdministrativeActionBatchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);
}

public interface IAdministrativeActionBatchJobProcessor
{
    Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken);
}
