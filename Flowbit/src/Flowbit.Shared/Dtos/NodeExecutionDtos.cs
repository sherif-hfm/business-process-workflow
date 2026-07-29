using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flowbit.Shared.Dtos;

/// <summary>
/// One durable visit to a workflow node. Normal visits are correlated to an
/// execution token; multi-instance user tasks instead expose one row per child
/// work item and deliberately do not expose a duplicate parent visit.
/// </summary>
public record NodeExecutionSummaryDto
{
    public required long Id { get; init; }
    public required long InstanceId { get; init; }
    public required long WorkflowId { get; init; }
    public required string WorkflowKey { get; init; }
    public required string WorkflowName { get; init; }
    public required int WorkflowVersion { get; init; }
    public string? BusinessKey { get; init; }

    public required long TokenId { get; init; }
    public long? UserTaskId { get; init; }
    public long? MultiInstanceExecutionId { get; init; }
    public int? ItemIndex { get; init; }
    public long? EntryGatewayBranchId { get; init; }
    public long? ExitGatewayBranchId { get; init; }

    public required string ExecutionKind { get; init; }
    public required int NodeId { get; init; }
    public required string NodeName { get; init; }
    public string? NodeExternalId { get; init; }
    public required string NodeType { get; init; }
    public required string Status { get; init; }
    public required string InstanceStatus { get; init; }
    public string? CompletionReason { get; init; }
    public required bool IsMultiInstance { get; init; }

    /// <summary>
    /// Effective user-task owner. An explicit assignment takes precedence over a
    /// claim; non-user-task executions have no owner.
    /// </summary>
    public string? Owner { get; init; }

    public int? EnteredViaFlowId { get; init; }
    public int? SelectedFlowId { get; init; }
    public int? ExitedViaFlowId { get; init; }

    /// <summary>
    /// The winning aggregate flow of the owning multi-instance execution.
    /// This is distinct from a child item's selected flow.
    /// </summary>
    public int? AggregateFlowId { get; init; }

    /// <summary>The causal actor that opened or activated this visit.</summary>
    public string? StartedBy { get; init; }
    public string? CompletedBy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DelegatedTaskAccessDto? StartedDelegatedAccess { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DelegatedTaskAccessDto? CompletedDelegatedAccess { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Elapsed milliseconds from StartedAt to CompletedAt, or to the search
    /// query's captured time for a currently active execution. Pending rows
    /// have no duration.
    /// </summary>
    public long? DurationMilliseconds { get; init; }

    public required bool IsCutoverSeeded { get; init; }
}

/// <summary>
/// Immutable and current multi-instance context associated with one child node
/// execution. The child result remains on the detail DTO; AggregateFlowId is the
/// execution-wide outcome, when one has been selected.
/// </summary>
public sealed record NodeExecutionMultiInstanceDto(
    long Id,
    string Mode,
    string Source,
    bool OnePerActor,
    string ResultVariable,
    string Status,
    int TotalCount,
    int CompletedCount,
    int CancelledCount,
    int? AggregateFlowId,
    string? CompletionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>A committed failure associated with a node execution.</summary>
public sealed record NodeExecutionErrorDto(
    string? Code,
    string? Description);

/// <summary>
/// One variable write attributed to this execution. SourceActionId retains the
/// runtime write source: it may identify a sequence flow for a user action or a
/// node/boundary for automatic and message writes.
/// </summary>
public sealed record NodeExecutionVariableChangeDto(
    long Id,
    string VariableName,
    int? SourceActionId,
    string? SetBy,
    JsonElement Value,
    DateTimeOffset SetAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DelegatedTaskAccessDto? DelegatedAccess { get; init; }
}

/// <summary>
/// Authorized detail for one node execution. VariableChanges contains only
/// writes explicitly attributed to this execution; it is not an instance
/// snapshot and does not include unrelated historical or current values.
/// </summary>
public sealed record NodeExecutionDetailDto : NodeExecutionSummaryDto
{
    /// <summary>
    /// Immutable node-role snapshot. Null means the snapshot was unavailable at
    /// migration cutover; an empty list means the node was known to have no roles.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NodeRoles { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? StartedByRoles { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? CompletedByRoles { get; init; }

    public bool? RequiresClaim { get; init; }
    public bool? RequiresAssignment { get; init; }
    public string? AssignedTo { get; init; }
    public string? ClaimedBy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ItemValue { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? SubmittedResult { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeExecutionMultiInstanceDto? MultiInstance { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeExecutionErrorDto? Error { get; init; }

    public required IReadOnlyList<NodeExecutionVariableChangeDto> VariableChanges { get; init; }
}
