using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IWorkflowJobRepository
{
    Task<WorkflowJobRecord> EnqueueAsync(
        WorkflowJobCreateRecord create,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a non-runnable job and its open incident atomically. Instance-
    /// owned callers must already hold the owning instance and token locks.
    /// This is used when work must be paused before it is ever leased.
    /// </summary>
    Task<WorkflowJobRecord> EnqueueIncidentAsync(
        WorkflowJobCreateRecord create,
        string type,
        string summary,
        string? details,
        CancellationToken cancellationToken);

    Task<WorkflowJobRecord?> GetAsync(long jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks exactly one job row. A workflow-state caller must acquire the
    /// owning instance lock before invoking this method.
    /// </summary>
    Task<WorkflowJobRecord?> GetForUpdateAsync(long jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowJobLeaseRecord>> LeaseRunnableAsync(
        WorkflowJobLeaseRequest request,
        CancellationToken cancellationToken);

    Task<bool> HeartbeatAsync(
        WorkflowJobFence fence,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Performs a non-locking fence check so a worker can promptly stop local
    /// work after cancellation or lease loss without renewing the lease.
    /// </summary>
    Task<bool> IsLeaseAliveAsync(
        WorkflowJobFence fence,
        CancellationToken cancellationToken);

    Task<WorkflowJobSnapshotRecord?> SaveStageAsync(
        WorkflowJobFence fence,
        WorkflowJobStageRecord stage,
        int maxSnapshotBytes,
        CancellationToken cancellationToken);

    Task<WorkflowJobSnapshotRecord?> GetSnapshotAsync(
        long snapshotId,
        CancellationToken cancellationToken);

    Task<bool> SaveResultReadyAsync(
        WorkflowJobFence fence,
        WorkflowJobResultRecord result,
        CancellationToken cancellationToken);

    Task<bool> ReleaseResultReadyLeaseAsync(
        WorkflowJobFence fence,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        WorkflowJobFence fence,
        CancellationToken cancellationToken);

    Task<bool> ScheduleRetryAsync(
        WorkflowJobFence fence,
        DateTimeOffset dueAt,
        WorkflowJobResultRecord failure,
        CancellationToken cancellationToken);

    Task<WorkflowIncidentRecord?> OpenIncidentAsync(
        WorkflowJobFence fence,
        string type,
        string summary,
        string? details,
        CancellationToken cancellationToken);

    Task<int> CancelByInstanceAsync(
        long instanceId,
        string reason,
        CancellationToken cancellationToken);

    Task<int> CancelByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        string reason,
        CancellationToken cancellationToken);

    Task<int> CancelByTimerSubscriptionIdsAsync(
        IReadOnlyCollection<long> timerSubscriptionIds,
        string reason,
        CancellationToken cancellationToken);

    Task<int> CancelOtherJobsByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        long? exceptJobId,
        string reason,
        CancellationToken cancellationToken);

    Task<int> CancelTimerJobsByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        long? exceptJobId,
        string reason,
        CancellationToken cancellationToken);

    Task<long> CountOpenByInstanceAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<WorkflowJobQueueStatisticsRecord> GetQueueStatisticsAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the earliest future due time or lease-expiry time that can make
    /// durable work runnable. PostgreSQL notifications remain wake-up hints;
    /// this value lets an idle dispatcher poll authoritatively at the exact
    /// next durable deadline instead of waiting for the idle backoff.
    /// </summary>
    Task<DateTimeOffset?> GetNextWakeAtAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, WorkflowInstanceJobSummaryRecord>>
        GetInstanceJobSummariesAsync(
            IReadOnlyCollection<long> instanceIds,
            CancellationToken cancellationToken);

    Task<PagedResult<WorkflowJobRecord>> SearchJobsAsync(
        WorkflowJobQuery query,
        CancellationToken cancellationToken);

    Task<PagedResult<WorkflowJobAttemptRecord>> ListAttemptsAsync(
        long jobId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResult<WorkflowIncidentRecord>> SearchIncidentsAsync(
        WorkflowIncidentQuery query,
        CancellationToken cancellationToken);

    Task<WorkflowIncidentRecord?> GetIncidentAsync(
        long incidentId,
        CancellationToken cancellationToken);

    Task<WorkflowJobRecord?> RetryIncidentAsync(
        long incidentId,
        string resolvedBy,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken);

    Task<WorkflowJobCleanupResult> CleanupAsync(
        DateTimeOffset completedJobsBefore,
        DateTimeOffset resolvedIncidentsBefore,
        int batchSize,
        CancellationToken cancellationToken);
}

public interface ITimerSubscriptionRepository
{
    Task<TimerSubscriptionRecord> CreateAsync(
        TimerSubscriptionCreateRecord create,
        CancellationToken cancellationToken);

    Task<TimerSubscriptionRecord?> GetForUpdateAsync(
        long subscriptionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TimerSubscriptionRecord>> ListForActivationAsync(
        long tokenId,
        Guid activationId,
        CancellationToken cancellationToken);

    Task<bool> AdvanceAsync(
        long subscriptionId,
        long expectedOccurrence,
        long nextOccurrence,
        DateTimeOffset nextDueAt,
        bool complete,
        CancellationToken cancellationToken);

    Task<bool> PauseAsync(
        long subscriptionId,
        long expectedOccurrence,
        CancellationToken cancellationToken);

    Task<int> CancelByInstanceAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<int> CancelByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken);

    Task<int> CancelOtherForTokenAsync(
        long instanceId,
        long tokenId,
        long exceptSubscriptionId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runtime-owned processor invoked by the standalone worker. It performs the
/// stage/invoke/result/finalize protocol for one fenced lease.
/// </summary>
public interface IWorkflowJobProcessor
{
    Task ProcessAsync(WorkflowJobLeaseRecord lease, CancellationToken cancellationToken);
}
