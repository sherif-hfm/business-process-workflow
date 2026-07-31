namespace Flowbit.Shared.Dtos;

/// <summary>
/// Bounded operations projection for a durable workflow job. Snapshots, result
/// payloads, stack traces, and attempt collections are intentionally excluded.
/// </summary>
public sealed record JobSummaryDto(
    long Id,
    long? InstanceId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    long? TokenId,
    int NodeId,
    string NodeName,
    string NodeType,
    string Kind,
    string Phase,
    string QueueClass,
    string Status,
    int AttemptCount,
    DateTimeOffset DueAt,
    DateTimeOffset? LeaseExpiresAt,
    long? IncidentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Administrative detail for one job without exposing its immutable execution
/// snapshot or persisted result payload.
/// </summary>
public sealed record JobDetailDto(
    JobSummaryDto Summary,
    Guid? ActivationId,
    string? WorkerId,
    long LeaseGeneration,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ResultReadyAt,
    string? LastFailureCode,
    string? LastFailureDescription);

/// <summary>
/// One separately paged durable-job attempt.
/// </summary>
public sealed record JobAttemptDto(
    long Id,
    long JobId,
    int AttemptNumber,
    string Status,
    string? WorkerId,
    long LeaseGeneration,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? FailureCode,
    string? FailureDescription);

/// <summary>
/// Bounded operations projection for an unresolved or historical workflow
/// incident. JobId is the immutable originating job identity; after the
/// 30-day job retention window a resolved incident can remain without a live
/// job detail until its own 90-day retention window expires. Detailed
/// diagnostics are available only from the detail endpoint.
/// </summary>
public sealed record IncidentSummaryDto(
    long Id,
    long JobId,
    long? InstanceId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    int NodeId,
    string NodeName,
    string Type,
    string Status,
    string Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record IncidentDetailDto(
    IncidentSummaryDto Summary,
    JobSummaryDto? Job,
    string? Details,
    string? ResolvedBy);

public sealed record RetryIncidentResultDto(
    long IncidentId,
    long JobId,
    string IncidentStatus,
    string JobStatus,
    DateTimeOffset DueAt);

/// <summary>
/// Constant-size administrative snapshot of durable queue health.
/// QueueLagSeconds is measured from the oldest currently runnable job.
/// </summary>
public sealed record JobQueueStatisticsDto(
    long RunnableDepth,
    DateTimeOffset? OldestRunnableDueAt,
    double QueueLagSeconds,
    long TimerControlRunnableCount,
    long ActiveLeaseCount,
    long OpenIncidentCount,
    DateTimeOffset ObservedAt);

public sealed record InstanceJobSummaryDto(
    long OpenCount,
    long QueuedCount,
    long RunningCount,
    long IncidentCount,
    DateTimeOffset? NearestDueAt);
