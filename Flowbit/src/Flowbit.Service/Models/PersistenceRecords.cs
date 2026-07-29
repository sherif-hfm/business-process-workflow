using System.Text.Json;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Models;

public sealed record WorkflowDefinitionRecord(
    long Id,
    string Name,
    string WorkflowKey,
    int Version,
    WorkflowModel Definition,
    bool IsPublished,
    bool IsDefault,
    DateTimeOffset CreatedAt);

public sealed record WorkflowInstanceRecord(
    long Id,
    long WorkflowDefinitionId,
    string WorkflowKey,
    string? IdempotencyKey,
    string? BusinessKey,
    string? BusinessKeyUniqueness,
    long ActiveTokenId,
    int CurrentStepId,
    long? ActiveUserTaskId,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? FaultCode = null,
    string? FaultDescription = null,
    long? CurrentNodeExecutionId = null);

// Snapshot copied onto an execution token and, for userTask nodes, its work item.
public sealed record CurrentNodeSnapshot(
    int Id,
    string Name,
    string? ExternalId,
    string Type,
    IReadOnlyList<string> Roles,
    bool RequiresClaim,
    bool RequiresAssignment,
    string? Assignee,
    bool IsMultiInstance = false,
    string? FaultCode = null,
    string? FaultDescription = null);

public sealed record ExecutionTokenRecord(
    long Id,
    long InstanceId,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    string NodeType,
    string? FaultCode,
    string? FaultDescription,
    string Status,
    long? GatewayBranchId,
    int? ArrivedViaFlowId,
    long? ComplexGatewayStateId,
    int? ComplexGatewayCycle,
    IReadOnlyList<long> ComplexDrainStateIds,
    string? TerminationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long? CurrentNodeExecutionId = null);

public sealed record NodeExecutionActorRecord(
    string? User,
    IReadOnlyList<string> Roles)
{
    public string? ActingFor { get; init; }
    public long? DelegationId { get; init; }
}

public sealed record NodeExecutionCompletionRecord(
    string Status,
    string CompletionReason,
    int? SelectedFlowId,
    int? ExitedViaFlowId,
    long? ExitGatewayBranchId,
    NodeExecutionActorRecord Actor,
    string? ErrorCode = null,
    string? ErrorDescription = null,
    bool HasExitGatewayBranchSnapshot = false);

public sealed record NodeExecutionRecord(
    long Id,
    long InstanceId,
    long ExecutionTokenId,
    long? UserTaskId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    string NodeType,
    string ExecutionKind,
    string Status,
    string? CompletionReason,
    long? EntryGatewayBranchId,
    long? ExitGatewayBranchId,
    int? EnteredViaFlowId,
    int? SelectedFlowId,
    int? ExitedViaFlowId,
    IReadOnlyList<string>? NodeRoles,
    string? TriggeredBy,
    IReadOnlyList<string>? TriggeredByRoles,
    string? CompletedBy,
    IReadOnlyList<string>? CompletedByRoles,
    string? ErrorCode,
    string? ErrorDescription,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    bool IsCutoverSeeded)
{
    public string? TriggeredActingFor { get; init; }
    public long? TriggeredDelegationId { get; init; }
    public string? CompletedActingFor { get; init; }
    public long? CompletedDelegationId { get; init; }
}

public sealed record GatewayExecutionRecord(
    long Id,
    long InstanceId,
    int GatewayNodeId,
    string GatewayType,
    string Direction,
    string? Phase,
    int? Cycle,
    IReadOnlyList<int> SelectedFlowIds,
    long? ParentBranchId,
    string Status,
    string? CompletionReason,
    int? InterruptingNodeId,
    long? InterruptingTokenId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record GatewayBranchRecord(
    long Id,
    long ExecutionId,
    int OriginatingFlowId,
    int Ordinal,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ComplexGatewayStateRecord(
    long Id,
    long InstanceId,
    int GatewayNodeId,
    string Phase,
    int Cycle,
    IReadOnlyList<int> ContributingFlowIds,
    IReadOnlyList<int> RemainingFlowIds,
    IReadOnlyList<long> ActivationDrainStateIds,
    IReadOnlyList<long> DrainingTokenIds,
    long? ActiveExecutionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MultiInstanceExecutionRecord(
    long Id,
    long InstanceId,
    long TokenId,
    int NodeId,
    string Mode,
    string Source,
    bool OnePerActor,
    string ResultVariable,
    string Status,
    int TotalCount,
    int CompletedCount,
    int CancelledCount,
    int? WinningFlowId,
    string? CompletionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record UserTaskRecord(
    long Id,
    long InstanceId,
    long TokenId,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    IReadOnlyList<string> Roles,
    bool RequiresClaim,
    bool RequiresAssignment,
    string Status,
    string? ClaimedBy,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    JsonElement? ItemValue,
    string? Assignee,
    int? SelectedFlowId,
    Dictionary<string, JsonElement>? Result,
    string? CompletedBy,
    IReadOnlyList<string>? CompletedByRoles,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    long? NodeExecutionId = null)
{
    public string? CompletedActingFor { get; init; }
    public long? CompletionDelegationId { get; init; }

    /// <summary>
    /// Request-local delegation metadata populated by actor-scoped repository
    /// queries. It is not part of the persisted user-task row.
    /// </summary>
    public string? ActingFor { get; init; }
    public long? DelegationId { get; init; }
}

public sealed record ManagedUserTaskRecord(
    long UserTaskId,
    long InstanceId,
    long TokenId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    string WorkflowName,
    int WorkflowVersion,
    string? BusinessKey,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    IReadOnlyList<string> NodeRoles,
    bool RequiresClaim,
    bool RequiresAssignment,
    string? ClaimedBy,
    string? Assignee,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    JsonElement? ItemValue,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, JsonElement>? Variables);

public sealed record UserTaskWorkSummaryRecord(
    long InstanceId,
    bool IsMultiInstance,
    int ActiveCount,
    int PendingCount,
    int ClaimedCount,
    int AssignedCount,
    string? SoleClaimedBy,
    string? SoleAssignee,
    int NormalTaskCount,
    int MultiInstanceTaskCount);

public sealed record MultiInstanceProgressRecord(
    MultiInstanceExecutionRecord Execution,
    int ActiveCount,
    int PendingCount,
    int CancelledCount,
    IReadOnlyDictionary<int, int> FlowCounts);

public sealed record MultiInstanceActorStateRecord(
    bool HasCompleted,
    long? OwnedTaskId);

// Compatibility projection for the existing instance-oriented API. TokenId and
// UserTaskId keep the persistence boundary ready for task/token-addressed APIs.
public sealed record InstanceListItem(
    long Id,
    long WorkflowId,
    long WorkflowDefinitionId,
    string WorkflowName,
    int WorkflowVersion,
    string? BusinessKey,
    string? BusinessKeyUniqueness,
    long TokenId,
    long? UserTaskId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    JsonElement? ItemValue,
    string? Assignee,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    string CurrentNodeType,
    IReadOnlyList<string> CurrentNodeRoles,
    bool CurrentRequiresClaim,
    bool CurrentRequiresAssignment,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    UserTaskWorkSummaryRecord? UserTasks,
    IReadOnlyDictionary<string, JsonElement>? Variables,
    MultiInstanceProgressRecord? MultiInstanceProgress = null,
    string? FaultCode = null,
    string? FaultDescription = null);

public sealed record BusinessKeyReservationRecord(bool Reserved, long? ExistingInstanceId);

public sealed record IdempotencyReservationRecord(bool Reserved, long? ExistingInstanceId);

public sealed record MessageDeliveryReceiptRecord(
    long InstanceId,
    string IdempotencyKey,
    long WaitHistoryId,
    int SourceNodeId,
    string CorrelationHeaderName,
    short ProofVersion,
    byte[] CredentialProofSalt,
    byte[] CredentialProofHash,
    byte[] EnvelopeProofSalt,
    byte[] EnvelopeProofHash,
    DateTimeOffset CreatedAt);

// Exact-match filter over an instance variable's scalar value (name = value).
public sealed record VariableFilter(string Name, string Value);

public enum SortDirection
{
    Ascending,
    Descending
}

public enum InstanceSortField
{
    Id,
    CreatedAt,
    UpdatedAt
}

public sealed record InstanceSortCriterion(
    InstanceSortField Field,
    SortDirection Direction);

public enum InboxSortField
{
    UserTaskId,
    InstanceId,
    TaskCreatedAt,
    TaskUpdatedAt,
    InstanceCreatedAt,
    InstanceUpdatedAt
}

public sealed record InboxSortCriterion(
    InboxSortField Field,
    SortDirection Direction);

public sealed record InboxListItem(
    long InstanceId,
    long WorkflowId,
    long WorkflowDefinitionId,
    string WorkflowName,
    int WorkflowVersion,
    string? BusinessKey,
    string? BusinessKeyUniqueness,
    long TokenId,
    long UserTaskId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    JsonElement? ItemValue,
    string? Assignee,
    int CurrentNodeId,
    string CurrentNodeName,
    string? CurrentNodeExternalId,
    string CurrentNodeType,
    IReadOnlyList<string> CurrentNodeRoles,
    bool CurrentRequiresClaim,
    bool CurrentRequiresAssignment,
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset TaskCreatedAt,
    DateTimeOffset TaskUpdatedAt,
    DateTimeOffset InstanceCreatedAt,
    DateTimeOffset InstanceUpdatedAt,
    IReadOnlyDictionary<string, JsonElement>? Variables,
    MultiInstanceProgressRecord? MultiInstanceProgress = null)
{
    public string? ActingFor { get; init; }
    public long? DelegationId { get; init; }
}

public sealed record AssignmentInheritanceSourceRecord(
    long UserTaskId,
    int NodeId,
    string? Assignee,
    string? CompletedBy,
    string? CompletedActingFor = null);

public sealed record InstanceVariableRecord(
    long Id,
    long InstanceId,
    string VariableName,
    int? SourceActionId,
    string? SetBy,
    JsonElement Value,
    DateTimeOffset SetAt,
    long? NodeExecutionId = null,
    string? ActingFor = null,
    long? DelegationId = null);

public sealed record InstanceHistoryRecord(
    long Id,
    long InstanceId,
    long? TokenId,
    long? UserTaskId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    int? ActionId,
    int FromStepId,
    int ToStepId,
    string? PerformedBy,
    Dictionary<string, JsonElement>? Payload,
    string? Note,
    DateTimeOffset PerformedAt,
    string? ActingFor = null,
    long? DelegationId = null);

public sealed record SequenceFlowOccurrenceWriteRecord(
    long InstanceId,
    int SequenceFlowId,
    int SourceNodeId,
    int TargetNodeId,
    long? TokenId,
    long? UserTaskId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    string Kind,
    bool IsAction,
    bool IsTraversal,
    string? User,
    IReadOnlyList<string> UserRoles,
    Dictionary<string, JsonElement>? Values,
    DateTimeOffset OccurredAt,
    string? ActingFor = null,
    long? DelegationId = null);

public sealed record SequenceFlowEvidenceRecord(
    string? User,
    IReadOnlyList<string> UserRoles,
    DateTimeOffset OccurredAt,
    string Kind,
    Dictionary<string, JsonElement>? Values,
    string? ActingFor = null,
    long? DelegationId = null);

public sealed record SequenceFlowSummaryRecord(
    long InstanceId,
    int SequenceFlowId,
    long ActionCount,
    SequenceFlowEvidenceRecord? LastAction,
    long TraversalCount,
    SequenceFlowEvidenceRecord? LastTraversal);

public sealed record EngineSettingRecord(
    long Id,
    string? Namespace,
    string Key,
    string Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class WorkflowInstanceStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    // Terminal status set when an instance enters an errorEndEvent (vs the
    // Completed status set by a plain endEvent). Filterable in the list/inbox.
    public const string Faulted = "faulted";
}

public static class UserTaskRecordStatuses
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

public static class MultiInstanceRecordStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
}

public static class ExecutionTokenRecordStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Faulted = "faulted";
    public const string Cancelled = "cancelled";
    public const string Merged = "merged";
}

public static class NodeExecutionRecordKinds
{
    public const string Node = "node";
    public const string UserTaskItem = "userTaskItem";
}

public static class NodeExecutionRecordStatuses
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Faulted = "faulted";
    public const string Merged = "merged";
}

public static class NodeExecutionCompletionReasons
{
    public const string Normal = "normal";
    public const string UserAction = "userAction";
    public const string MessageDelivery = "messageDelivery";
    public const string MultiInstanceItem = "multiInstanceItem";
    public const string MultiInstanceCompleted = "multiInstanceCompleted";
    public const string MultiInstanceInterrupt = "multiInstanceInterrupt";
    public const string BoundaryCaught = "boundaryCaught";
    public const string NormalEnd = "normalEnd";
    public const string TerminateEnd = "terminateEnd";
    public const string ErrorEnd = "errorEnd";
    public const string InstanceCancelled = "instanceCancelled";
    public const string GatewayScopeCancelled = "gatewayScopeCancelled";
    public const string GatewayJoinMerged = "gatewayJoinMerged";
    public const string ParallelFork = "parallelFork";
    public const string ParallelJoin = "parallelJoin";
    public const string InclusiveSplit = "inclusiveSplit";
    public const string InclusiveMerge = "inclusiveMerge";
    public const string ComplexActivation = "complexActivation";
    public const string ComplexReset = "complexReset";
    public const string ScopedInterrupt = "scopedInterrupt";
    public const string ScopedInterruptSkipped = "scopedInterruptSkipped";
}

public static class ExecutionTokenTerminationReasons
{
    public const string NormalEnd = "normalEnd";
    public const string TerminateEnd = "terminateEnd";
    public const string ErrorEnd = "errorEnd";
    public const string InstanceCancelled = "instanceCancelled";
    public const string GatewayScopeCancelled = "gatewayScopeCancelled";
    public const string GatewayJoinMerged = "gatewayJoinMerged";
}

public static class GatewayExecutionRecordStatuses
{
    public const string Active = "active";
    public const string Joined = "joined";
    public const string Completed = "completed";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
}

public static class GatewayBranchRecordStatuses
{
    public const string Active = "active";
    public const string Merged = "merged";
    public const string Completed = "completed";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
}

public static class GatewayExecutionRecordDirections
{
    public const string Split = "split";
    public const string Merge = "merge";
}

public static class ComplexGatewayStateRecordPhases
{
    public const string WaitingForStart = "waitingForStart";
    public const string WaitingForReset = "waitingForReset";
    public const string InterruptedDraining = "interruptedDraining";
}
