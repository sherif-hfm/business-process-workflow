namespace Flowbit.Infrastructure.Entities;

public sealed class ParallelGatewayExecutionEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public int ForkNodeId { get; set; }
    public long? ParentBranchId { get; set; }
    public ParallelGatewayBranchEntity? ParentBranch { get; set; }
    public string Status { get; set; } = ParallelGatewayExecutionStatuses.Active;
    public string? CompletionReason { get; set; }
    public int? InterruptingNodeId { get; set; }
    public long? InterruptingTokenId { get; set; }
    public ExecutionTokenEntity? InterruptingToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<ParallelGatewayBranchEntity> Branches { get; set; } = [];
}

public static class ParallelGatewayExecutionStatuses
{
    public const string Active = "active";
    public const string Joined = "joined";
    public const string Completed = "completed";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
}
