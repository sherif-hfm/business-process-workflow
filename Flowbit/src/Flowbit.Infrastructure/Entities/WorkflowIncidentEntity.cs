namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowIncidentEntity
{
    public long Id { get; set; }
    public long? JobId { get; set; }
    public long OriginalJobId { get; set; }
    public WorkflowJobEntity? Job { get; set; }
    public long? InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }
    public string WorkflowKey { get; set; } = string.Empty;
    public int NodeId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = "open";
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
