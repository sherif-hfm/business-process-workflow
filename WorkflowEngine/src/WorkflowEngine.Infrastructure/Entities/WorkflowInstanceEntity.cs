namespace WorkflowEngine.Infrastructure.Entities;

public sealed class WorkflowInstanceEntity
{
    public long Id { get; set; }

    public long WorkflowDefinitionId { get; set; }

    public string WorkflowKey { get; set; } = string.Empty;

    public string? BusinessKey { get; set; }

    public string? BusinessKeyUniqueness { get; set; }

    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }

    public string Status { get; set; } = "running";

    public string? StartedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<InstanceVariableEntity> Variables { get; set; } = [];

    public List<InstanceHistoryEntity> History { get; set; } = [];

    public List<ExecutionTokenEntity> Tokens { get; set; } = [];

    public List<UserTaskEntity> UserTasks { get; set; } = [];

    public List<MultiInstanceExecutionEntity> MultiInstanceExecutions { get; set; } = [];
}
