namespace WorkflowEngine.Infrastructure.Entities;

public sealed class MultiInstanceExecutionEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public long TokenId { get; set; }
    public ExecutionTokenEntity? Token { get; set; }
    public int NodeId { get; set; }
    public string Mode { get; set; } = "parallel";
    public string Source { get; set; } = "collection";
    public string ResultVariable { get; set; } = string.Empty;
    public string Status { get; set; } = MultiInstanceExecutionStatuses.Active;
    public int TotalCount { get; set; }
    public int CompletedCount { get; set; }
    public int CancelledCount { get; set; }
    public int? WinningFlowId { get; set; }
    public string? CompletionReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<MultiInstanceFlowCountEntity> FlowCounts { get; set; } = [];
    public List<UserTaskEntity> UserTasks { get; set; } = [];
}

public static class MultiInstanceExecutionStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
}
