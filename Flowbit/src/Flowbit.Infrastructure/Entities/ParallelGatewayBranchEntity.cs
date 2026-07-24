namespace Flowbit.Infrastructure.Entities;

public sealed class ParallelGatewayBranchEntity
{
    public long Id { get; set; }
    public long ExecutionId { get; set; }
    public ParallelGatewayExecutionEntity? Execution { get; set; }
    public int OriginatingFlowId { get; set; }
    public int Ordinal { get; set; }
    public string Status { get; set; } = ParallelGatewayBranchStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<ExecutionTokenEntity> Tokens { get; set; } = [];
    public List<ParallelGatewayExecutionEntity> ChildExecutions { get; set; } = [];
}

public static class ParallelGatewayBranchStatuses
{
    public const string Active = "active";
    public const string Merged = "merged";
    public const string Completed = "completed";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
}
