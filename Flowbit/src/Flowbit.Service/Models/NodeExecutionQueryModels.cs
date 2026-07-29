namespace Flowbit.Service.Models;

/// <summary>
/// Raw API search input. The service validates and canonicalizes this contract
/// before handing it to the persistence boundary.
/// </summary>
public sealed record NodeExecutionSearchRequest
{
    public long? ExecutionId { get; init; }
    public long? InstanceId { get; init; }
    public long? WorkflowId { get; init; }
    public string? WorkflowKey { get; init; }
    public int? WorkflowVersion { get; init; }
    public string? BusinessKey { get; init; }
    public long? TokenId { get; init; }
    public long? UserTaskId { get; init; }
    public long? MultiInstanceExecutionId { get; init; }
    public long? GatewayBranchId { get; init; }
    public int? ItemIndex { get; init; }

    public string? ExecutionKind { get; init; }
    public int? NodeId { get; init; }
    public string? NodeName { get; init; }
    public string? NodeExternalId { get; init; }
    public IReadOnlyList<string>? NodeTypes { get; init; }
    public IReadOnlyList<string>? Statuses { get; init; }
    public IReadOnlyList<string>? InstanceStatuses { get; init; }
    public IReadOnlyList<string>? CompletionReasons { get; init; }
    public bool? IsMultiInstance { get; init; }
    public bool? IsCutoverSeeded { get; init; }

    public string? Owner { get; init; }
    public string? StartedBy { get; init; }
    public string? CompletedBy { get; init; }
    public int? EnteredViaFlowId { get; init; }
    public int? SelectedFlowId { get; init; }
    public int? ExitedViaFlowId { get; init; }
    public int? AggregateFlowId { get; init; }

    public DateTimeOffset? CreatedFrom { get; init; }
    public DateTimeOffset? CreatedTo { get; init; }
    public DateTimeOffset? StartedFrom { get; init; }
    public DateTimeOffset? StartedTo { get; init; }
    public DateTimeOffset? UpdatedFrom { get; init; }
    public DateTimeOffset? UpdatedTo { get; init; }
    public DateTimeOffset? CompletedFrom { get; init; }
    public DateTimeOffset? CompletedTo { get; init; }
    public long? MinDurationMilliseconds { get; init; }
    public long? MaxDurationMilliseconds { get; init; }

    public IReadOnlyList<string>? Variables { get; init; }
    public IReadOnlyList<string>? Sort { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public enum NodeExecutionSortField
{
    Id,
    InstanceId,
    WorkflowId,
    NodeId,
    CreatedAt,
    StartedAt,
    UpdatedAt,
    CompletedAt,
    Duration
}

public sealed record NodeExecutionSortCriterion(
    NodeExecutionSortField Field,
    SortDirection Direction);

/// <summary>Validated, normalized repository query.</summary>
public sealed record NodeExecutionQuery
{
    public long? ExecutionId { get; init; }
    public long? InstanceId { get; init; }
    public long? WorkflowId { get; init; }
    public string? WorkflowKey { get; init; }
    public int? WorkflowVersion { get; init; }
    public string? BusinessKey { get; init; }
    public long? TokenId { get; init; }
    public long? UserTaskId { get; init; }
    public long? MultiInstanceExecutionId { get; init; }
    public long? GatewayBranchId { get; init; }
    public int? ItemIndex { get; init; }

    public string? ExecutionKind { get; init; }
    public int? NodeId { get; init; }
    public string? NodeName { get; init; }
    public string? NodeExternalId { get; init; }
    public required IReadOnlyList<string> NodeTypes { get; init; }
    public required IReadOnlyList<string> Statuses { get; init; }
    public required IReadOnlyList<string> InstanceStatuses { get; init; }
    public required IReadOnlyList<string> CompletionReasons { get; init; }
    public bool? IsMultiInstance { get; init; }
    public bool? IsCutoverSeeded { get; init; }

    public string? Owner { get; init; }
    public string? StartedBy { get; init; }
    public string? CompletedBy { get; init; }
    public int? EnteredViaFlowId { get; init; }
    public int? SelectedFlowId { get; init; }
    public int? ExitedViaFlowId { get; init; }
    public int? AggregateFlowId { get; init; }

    public DateTimeOffset? CreatedFrom { get; init; }
    public DateTimeOffset? CreatedTo { get; init; }
    public DateTimeOffset? StartedFrom { get; init; }
    public DateTimeOffset? StartedTo { get; init; }
    public DateTimeOffset? UpdatedFrom { get; init; }
    public DateTimeOffset? UpdatedTo { get; init; }
    public DateTimeOffset? CompletedFrom { get; init; }
    public DateTimeOffset? CompletedTo { get; init; }
    public long? MinDurationMilliseconds { get; init; }
    public long? MaxDurationMilliseconds { get; init; }

    public required IReadOnlyList<VariableFilter> VariableFilters { get; init; }
    public required IReadOnlyList<NodeExecutionSortCriterion> Sort { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
}

/// <summary>
/// SQL visibility scope. Workflow-role checks are still performed by the
/// repository against each immutable definition version.
/// </summary>
public sealed record NodeExecutionAuthorization(
    bool IsGlobalReader,
    IReadOnlyList<string> LowerCallerRoles);
