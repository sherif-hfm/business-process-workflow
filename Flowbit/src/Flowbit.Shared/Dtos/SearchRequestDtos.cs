using System.Text.Json;

namespace Flowbit.Shared.Dtos;

/// <summary>
/// One structured search sort clause. Each endpoint validates its own supported
/// field names and applies its existing default order when no clauses are supplied.
/// </summary>
public sealed record SearchSortDto(string Field, string Direction);

/// <summary>Advanced workflow-instance search request.</summary>
public sealed record InstanceSearchRequest
{
    public string? Status { get; init; }
    public long? InstanceId { get; init; }
    public long? WorkflowId { get; init; }
    public string? WorkflowKey { get; init; }
    public string? BusinessKey { get; init; }
    public int? NodeId { get; init; }
    public string? NodeExternalId { get; init; }
    public JsonElement? VariableFilter { get; init; }
    public IReadOnlyList<SearchSortDto>? Sort { get; init; }
    public string? Cursor { get; init; }
    public bool? IncludeVariables { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>Advanced actor inbox search request.</summary>
public sealed record InboxSearchRequest
{
    public long? InstanceId { get; init; }
    public long? WorkflowId { get; init; }
    public string? WorkflowKey { get; init; }
    public string? BusinessKey { get; init; }
    public int? NodeId { get; init; }
    public string? NodeExternalId { get; init; }
    public JsonElement? VariableFilter { get; init; }
    public IReadOnlyList<SearchSortDto>? Sort { get; init; }
    public bool? IncludeVariables { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>Advanced manager-scoped user-task search request.</summary>
public sealed record ManageableUserTaskSearchRequest
{
    public long? TaskId { get; init; }
    public long? InstanceId { get; init; }
    public long? WorkflowId { get; init; }
    public string? WorkflowKey { get; init; }
    public string? BusinessKey { get; init; }
    public int? NodeId { get; init; }
    public string? NodeExternalId { get; init; }
    public string? Owner { get; init; }
    public string? Ownership { get; init; }
    public JsonElement? VariableFilter { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>Advanced task-distribution search request.</summary>
public sealed record DistributableUserTaskSearchRequest
{
    public long? TaskId { get; init; }
    public long? InstanceId { get; init; }
    public long? WorkflowId { get; init; }
    public string? BusinessKey { get; init; }
    public int? NodeId { get; init; }
    public string? NodeExternalId { get; init; }
    public string? Owner { get; init; }
    public string? Ownership { get; init; }
    public JsonElement? VariableFilter { get; init; }
    public bool? IncludeVariables { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>Advanced cross-workflow node-execution search request.</summary>
public sealed record NodeExecutionSearchBodyRequest
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
    public JsonElement? VariableFilter { get; init; }
    public IReadOnlyList<SearchSortDto>? Sort { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}
