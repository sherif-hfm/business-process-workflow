using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowJobEntity
{
    public long Id { get; set; }
    public long? InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }
    public string WorkflowKey { get; set; } = string.Empty;
    public long? TokenId { get; set; }
    public ExecutionTokenEntity? Token { get; set; }
    public long? MultiInstanceExecutionId { get; set; }
    public MultiInstanceExecutionEntity? MultiInstanceExecution { get; set; }
    public long? UserTaskId { get; set; }
    public UserTaskEntity? UserTask { get; set; }
    public long? TimerSubscriptionId { get; set; }
    public TimerSubscriptionEntity? TimerSubscription { get; set; }
    public Guid ActivationId { get; set; }
    public int AutomaticActivationCount { get; set; }
    public int NodeId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string QueueClass { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public int Priority { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public string FailureHandling { get; set; } = "boundaryFirst";
    public TimeSpan[] RetryDelays { get; set; } = [];
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? ScheduledOccurrenceAt { get; set; }
    public JsonDocument? PayloadJson { get; set; }
    public long? SnapshotId { get; set; }
    public WorkflowJobSnapshotEntity? Snapshot { get; set; }
    public string? WorkerId { get; set; }
    public Guid? LeaseToken { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public JsonDocument? ResultJson { get; set; }
    public JsonDocument? ErrorJson { get; set; }
    public DateTimeOffset? ResultReadyAt { get; set; }
    public string? LastFailureCode { get; set; }
    public string? LastFailureDescription { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<WorkflowJobAttemptEntity> Attempts { get; set; } = [];
    public List<WorkflowIncidentEntity> Incidents { get; set; } = [];
}
