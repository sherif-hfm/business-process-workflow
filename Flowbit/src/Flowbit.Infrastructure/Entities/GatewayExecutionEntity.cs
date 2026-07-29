namespace Flowbit.Infrastructure.Entities;

public sealed class GatewayExecutionEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public int GatewayNodeId { get; set; }
    public string GatewayType { get; set; } = string.Empty;
    public string Direction { get; set; } = GatewayExecutionDirections.Split;
    public string? Phase { get; set; }
    public int? Cycle { get; set; }
    public int[] SelectedFlowIds { get; set; } = [];
    public long? ParentBranchId { get; set; }
    public GatewayBranchEntity? ParentBranch { get; set; }
    public string Status { get; set; } = GatewayExecutionStatuses.Active;
    public string? CompletionReason { get; set; }
    public int? InterruptingNodeId { get; set; }
    public long? InterruptingTokenId { get; set; }
    public ExecutionTokenEntity? InterruptingToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<GatewayBranchEntity> Branches { get; set; } = [];
    public List<ComplexGatewayStateEntity> ActiveComplexStates { get; set; } = [];
}

public static class GatewayExecutionDirections
{
    public const string Split = "split";
    public const string Merge = "merge";
}

public static class GatewayExecutionStatuses
{
    public const string Active = "active";
    public const string Joined = "joined";
    public const string Completed = "completed";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
}
