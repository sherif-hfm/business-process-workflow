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
    public long? GatewayBranchId { get; set; }
    public GatewayBranchEntity? GatewayBranch { get; set; }
    public long? ComplexGatewayStateId { get; set; }
    public ComplexGatewayStateEntity? ComplexGatewayState { get; set; }
    public int? ComplexGatewayCycle { get; set; }
    public long[] ComplexDrainStateIds { get; set; } = [];
    public int? ArrivedViaFlowId { get; set; }
    public string? TerminationReason { get; set; }
    public Guid ActivationId { get; set; } = Guid.NewGuid();
    public string? WaitState { get; set; }
    public long? WaitingJobId { get; set; }
    public long? WaitingTimerSubscriptionId { get; set; }
    public string Status { get; set; } = ExecutionTokenStatuses.Active;
    public long? CurrentNodeExecutionId { get; set; }
    public NodeExecutionEntity? CurrentNodeExecution { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<NodeExecutionEntity> NodeExecutions { get; set; } = [];
    public List<UserTaskEntity> UserTasks { get; set; } = [];
    public List<MultiInstanceExecutionEntity> MultiInstanceExecutions { get; set; } = [];
    public List<GatewayExecutionEntity> InterruptedGatewayExecutions { get; set; } = [];
    public List<WorkflowJobEntity> Jobs { get; set; } = [];
    public List<TimerSubscriptionEntity> TimerSubscriptions { get; set; } = [];
}

public static class ExecutionTokenStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Faulted = "faulted";
    public const string Cancelled = "cancelled";
    public const string Merged = "merged";
}
