using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Role-protected operations surface for durable jobs and incidents. Payloads,
/// snapshots, and full attempt collections remain behind dedicated persistence
/// APIs and are never included in list projections.
/// </summary>
public interface IWorkflowJobOperationsService
{
    Task<JobQueueStatisticsDto> GetQueueStatisticsAsync(
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<JobSummaryDto>> SearchJobsAsync(
        WorkflowJobQuery query,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<JobDetailDto?> GetJobAsync(
        long jobId,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<JobAttemptDto>> ListAttemptsAsync(
        long jobId,
        string? cursor,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<IncidentSummaryDto>> SearchIncidentsAsync(
        WorkflowIncidentQuery query,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<IncidentDetailDto?> GetIncidentAsync(
        long incidentId,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<RetryIncidentResultDto?> RetryIncidentAsync(
        long incidentId,
        ActorContext actor,
        CancellationToken cancellationToken);
}
