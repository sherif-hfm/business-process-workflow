namespace WorkflowEngine.Infrastructure.Entities;

public sealed class WorkflowInstanceEntity
{
    public long Id { get; set; }

    public long WorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }

    public int CurrentStepId { get; set; }

    public string Status { get; set; } = "running";

    public string? ClaimedBy { get; set; }

    public string? StartedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<InstanceVariableEntity> Variables { get; set; } = [];

    public List<InstanceHistoryEntity> History { get; set; } = [];
}
