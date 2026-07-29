namespace Flowbit.Infrastructure.Entities;

public sealed class ComplexGatewayStateEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public int GatewayNodeId { get; set; }
    public string Phase { get; set; } = ComplexGatewayStatePhases.WaitingForStart;
    public int Cycle { get; set; }
    public int[] ContributingFlowIds { get; set; } = [];
    public int[] RemainingFlowIds { get; set; } = [];
    public long[] ActivationDrainStateIds { get; set; } = [];
    public long[] DrainingTokenIds { get; set; } = [];
    public long? ActiveExecutionId { get; set; }
    public GatewayExecutionEntity? ActiveExecution { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ExecutionTokenEntity> WaitingTokens { get; set; } = [];
}

public static class ComplexGatewayStatePhases
{
    public const string WaitingForStart = "waitingForStart";
    public const string WaitingForReset = "waitingForReset";
    public const string InterruptedDraining = "interruptedDraining";
}
