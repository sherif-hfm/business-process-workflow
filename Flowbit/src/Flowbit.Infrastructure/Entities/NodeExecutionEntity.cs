using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class NodeExecutionEntity
{
    public long Id { get; set; }

    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }

    public long ExecutionTokenId { get; set; }
    public ExecutionTokenEntity? ExecutionToken { get; set; }

    public long? UserTaskId { get; set; }
    public UserTaskEntity? UserTask { get; set; }

    public long? MultiInstanceExecutionId { get; set; }
    public MultiInstanceExecutionEntity? MultiInstanceExecution { get; set; }

    public int? ItemIndex { get; set; }

    public int NodeId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string? NodeExternalId { get; set; }
    public string NodeType { get; set; } = string.Empty;

    public string ExecutionKind { get; set; } = NodeExecutionKinds.Node;
    public string Status { get; set; } = NodeExecutionStatuses.Active;
    public string? CompletionReason { get; set; }

    public long? EntryParallelBranchId { get; set; }
    public ParallelGatewayBranchEntity? EntryParallelBranch { get; set; }
    public long? ExitParallelBranchId { get; set; }
    public ParallelGatewayBranchEntity? ExitParallelBranch { get; set; }

    public int? EnteredViaFlowId { get; set; }
    public int? SelectedFlowId { get; set; }
    public int? ExitedViaFlowId { get; set; }

    public JsonDocument? NodeRolesJson { get; set; }

    public string? TriggeredBy { get; set; }
    public JsonDocument? TriggeredByRolesJson { get; set; }
    public string? TriggeredActingFor { get; set; }
    public long? TriggeredDelegationId { get; set; }

    public string? CompletedBy { get; set; }
    public JsonDocument? CompletedByRolesJson { get; set; }
    public string? CompletedActingFor { get; set; }
    public long? CompletedDelegationId { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorDescription { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsCutoverSeeded { get; set; }
}

public static class NodeExecutionKinds
{
    public const string Node = "node";
    public const string UserTaskItem = "userTaskItem";
}

public static class NodeExecutionStatuses
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Faulted = "faulted";
    public const string Merged = "merged";
}
