using System.Text.Json;

namespace Flowbit.Service.Models;

public static class WorkflowJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string ResultReady = "resultReady";
    public const string Retry = "retry";
    public const string Completed = "completed";
    public const string Incident = "incident";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";

    public static readonly IReadOnlySet<string> Terminal =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Completed,
            Cancelled,
            Skipped
        };
}

public static class WorkflowJobClasses
{
    public const string Control = "control";
    public const string Activity = "activity";
}

public static class WorkflowJobKinds
{
    public const string AsyncBefore = "asyncBefore";
    public const string AsyncAfter = "asyncAfter";
    public const string Timer = "timer";
    public const string TimerStart = "timerStart";
    public const string TimerBoundary = "timerBoundary";
}

public static class WorkflowJobFailureHandling
{
    public const string BoundaryFirst = "boundaryFirst";
    public const string RetryFirst = "retryFirst";
}

public static class WorkflowJobAttemptStatuses
{
    public const string Running = "running";
    public const string ResultReady = "resultReady";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string LeaseLost = "leaseLost";
    public const string Cancelled = "cancelled";
}

public static class WorkflowIncidentStatuses
{
    public const string Open = "open";
    public const string Resolved = "resolved";
}

public static class WorkflowIncidentTypes
{
    public const string AutomaticLoopLimit = "automatic_loop_limit";
}

public static class TimerSubscriptionStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

public static class TimerScheduleKinds
{
    public const string Date = "timeDate";
    public const string Duration = "timeDuration";
    public const string Cycle = "timeCycle";
}

public sealed record WorkflowJobCreateRecord
{
    public long? InstanceId { get; init; }
    public required long WorkflowDefinitionId { get; init; }
    public required string WorkflowKey { get; init; }
    public long? TokenId { get; init; }
    public long? MultiInstanceExecutionId { get; init; }
    public long? UserTaskId { get; init; }
    public long? TimerSubscriptionId { get; init; }
    public required Guid ActivationId { get; init; }
    public int AutomaticActivationCount { get; init; }
    public required int NodeId { get; init; }
    public required string NodeName { get; init; }
    public required string NodeType { get; init; }
    public required string Kind { get; init; }
    public required string QueueClass { get; init; }
    public required string Phase { get; init; }
    public required DateTimeOffset DueAt { get; init; }
    public int Priority { get; init; }
    public int MaxAttempts { get; init; } = 4;
    public string FailureHandling { get; init; } = WorkflowJobFailureHandling.BoundaryFirst;
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];
    public JsonElement? Payload { get; init; }
    public long? SnapshotId { get; init; }
    public DateTimeOffset? ScheduledOccurrenceAt { get; init; }
}

public sealed record WorkflowJobRecord(
    long Id,
    long? InstanceId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    long? TokenId,
    long? MultiInstanceExecutionId,
    long? UserTaskId,
    long? TimerSubscriptionId,
    Guid ActivationId,
    int NodeId,
    string NodeName,
    string NodeType,
    string Kind,
    string QueueClass,
    string Phase,
    string Status,
    int Priority,
    int AttemptCount,
    int MaxAttempts,
    string FailureHandling,
    IReadOnlyList<TimeSpan> RetryDelays,
    DateTimeOffset DueAt,
    DateTimeOffset? ScheduledOccurrenceAt,
    JsonElement? Payload,
    long? SnapshotId,
    string? WorkerId,
    Guid? LeaseToken,
    long LeaseGeneration,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? HeartbeatAt,
    JsonElement? Result,
    JsonElement? Error,
    string? LastFailureCode,
    string? LastFailureDescription,
    DateTimeOffset? ResultReadyAt,
    long? IncidentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int AutomaticActivationCount = 0);

public sealed record WorkflowJobLeaseRequest(
    string WorkerId,
    int MaxCount,
    int MaxActivityCount,
    int MaxPerInstance,
    TimeSpan LeaseDuration);

public sealed record WorkflowJobLeaseRecord(
    WorkflowJobRecord Job,
    Guid LeaseToken,
    long LeaseGeneration,
    int AttemptNumber);

public sealed record WorkflowJobFence(
    long JobId,
    string WorkerId,
    Guid LeaseToken,
    long LeaseGeneration);

public sealed record WorkflowJobStageRecord(
    JsonElement? Invocation,
    IReadOnlyDictionary<string, JsonElement> Variables,
    IReadOnlyDictionary<string, long> OutputVariableVersions,
    JsonElement? FlowInfo,
    DateTimeOffset EvaluationTime);

public sealed record WorkflowJobResultRecord(
    JsonElement? Result,
    JsonElement? Error,
    string? FailureCode,
    string? FailureDescription);

public sealed record WorkflowJobSnapshotRecord(
    long Id,
    string Kind,
    JsonElement? Invocation,
    IReadOnlyDictionary<string, JsonElement> Variables,
    IReadOnlyDictionary<string, long> OutputVariableVersions,
    JsonElement? FlowInfo,
    DateTimeOffset EvaluationTime,
    int SizeBytes,
    DateTimeOffset CreatedAt);

public sealed record WorkflowJobAttemptRecord(
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

public sealed record WorkflowIncidentRecord(
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
    string? Details,
    string? ResolvedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record WorkflowJobQueueStatisticsRecord(
    long RunnableDepth,
    DateTimeOffset? OldestRunnableDueAt,
    long TimerControlRunnableCount,
    long ActiveLeaseCount,
    long OpenIncidentCount,
    DateTimeOffset ObservedAt);

public sealed record WorkflowInstanceJobSummaryRecord(
    long InstanceId,
    long OpenCount,
    long QueuedCount,
    long RunningCount,
    long IncidentCount,
    DateTimeOffset? NearestDueAt);

public sealed record WorkflowJobQuery
{
    public long? InstanceId { get; init; }
    public long? WorkflowDefinitionId { get; init; }
    public string? WorkflowKey { get; init; }
    public long? TokenId { get; init; }
    public IReadOnlyList<string> Statuses { get; init; } = [];
    public IReadOnlyList<string> Kinds { get; init; } = [];
    public DateTimeOffset? DueFrom { get; init; }
    public DateTimeOffset? DueTo { get; init; }
    public string? Cursor { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record WorkflowIncidentQuery
{
    public long? InstanceId { get; init; }
    public long? WorkflowDefinitionId { get; init; }
    public string? WorkflowKey { get; init; }
    public IReadOnlyList<string> Statuses { get; init; } = [];
    public IReadOnlyList<string> Types { get; init; } = [];
    public string? Cursor { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record TimerSubscriptionCreateRecord
{
    public long? InstanceId { get; init; }
    public required long WorkflowDefinitionId { get; init; }
    public required string WorkflowKey { get; init; }
    public long? TokenId { get; init; }
    public required Guid ActivationId { get; init; }
    public required int TimerNodeId { get; init; }
    public required string TimerNodeName { get; init; }
    public int? AttachedToNodeId { get; init; }
    public required string ScheduleKind { get; init; }
    public required string ScheduleExpression { get; init; }
    public required bool CancelActivity { get; init; }
    public required DateTimeOffset NextDueAt { get; init; }
    public long Occurrence { get; init; }
}

public sealed record TimerSubscriptionRecord(
    long Id,
    long? InstanceId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    long? TokenId,
    Guid ActivationId,
    int TimerNodeId,
    string TimerNodeName,
    int? AttachedToNodeId,
    string ScheduleKind,
    string ScheduleExpression,
    bool CancelActivity,
    string Status,
    DateTimeOffset NextDueAt,
    long Occurrence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record WorkflowJobCleanupResult(
    int JobsDeleted,
    int AttemptsDeleted,
    int SnapshotsDeleted,
    int IncidentsDeleted);
