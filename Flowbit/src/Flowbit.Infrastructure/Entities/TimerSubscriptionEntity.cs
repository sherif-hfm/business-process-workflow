namespace Flowbit.Infrastructure.Entities;

public sealed class TimerSubscriptionEntity
{
    public long Id { get; set; }
    public long? InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }
    public string WorkflowKey { get; set; } = string.Empty;
    public long? TokenId { get; set; }
    public ExecutionTokenEntity? Token { get; set; }
    public Guid ActivationId { get; set; }
    public int TimerNodeId { get; set; }
    public string TimerNodeName { get; set; } = string.Empty;
    public int? AttachedToNodeId { get; set; }
    public string ScheduleKind { get; set; } = string.Empty;
    public string ScheduleExpression { get; set; } = string.Empty;
    public bool CancelActivity { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset NextDueAt { get; set; }
    public long Occurrence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<WorkflowJobEntity> Jobs { get; set; } = [];
}
