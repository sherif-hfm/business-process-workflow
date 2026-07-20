namespace Flowbit.Infrastructure.Entities;

public sealed class ExecutionTokenEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public int NodeId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string? NodeExternalId { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string? FaultCode { get; set; }
    public string? FaultDescription { get; set; }
    public string Status { get; set; } = ExecutionTokenStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<UserTaskEntity> UserTasks { get; set; } = [];
    public List<MultiInstanceExecutionEntity> MultiInstanceExecutions { get; set; } = [];
}

public static class ExecutionTokenStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Faulted = "faulted";
    public const string Cancelled = "cancelled";
}
