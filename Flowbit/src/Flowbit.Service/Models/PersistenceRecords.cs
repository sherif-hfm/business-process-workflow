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
    DateTimeOffset UpdatedAt);

// Snapshot copied onto an execution token and, for userTask nodes, its work item.
public sealed record CurrentNodeSnapshot(
    int Id,
    string Name,
    string? ExternalId,
    string Type,
    IReadOnlyList<string> Roles,
    bool RequiresClaim,
    string? Assignee,
    bool IsMultiInstance = false);

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
    DateTimeOffset? CompletedAt);

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
    string? SoleAssignee);

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
    string Status,
    string? ClaimedBy,
    string? StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    UserTaskWorkSummaryRecord? UserTasks,
    IReadOnlyDictionary<string, JsonElement>? Variables,
    MultiInstanceProgressRecord? MultiInstanceProgress = null);

public sealed record BusinessKeyReservationRecord(bool Reserved, long? ExistingInstanceId);

public sealed record IdempotencyReservationRecord(bool Reserved, long? ExistingInstanceId);

// Exact-match filter over an instance variable's scalar value (name = value).
public sealed record VariableFilter(string Name, string Value);

public sealed record InstanceVariableRecord(
    long Id,
    long InstanceId,
    string VariableName,
    int? SourceActionId,
    string? SetBy,
    JsonElement Value,
    DateTimeOffset SetAt);

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
    DateTimeOffset PerformedAt);

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
    DateTimeOffset OccurredAt);

public sealed record SequenceFlowEvidenceRecord(
    string? User,
    IReadOnlyList<string> UserRoles,
    DateTimeOffset OccurredAt,
    string Kind,
    Dictionary<string, JsonElement>? Values);

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
