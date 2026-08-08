using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flowbit.Shared.Dtos;

/// <summary>
/// Server-authoritative filters used to find running instances on one exact
/// workflow definition for a version-change batch.
/// </summary>
public sealed record InstanceVersionChangeCandidateFilterDto
{
    public long SourceWorkflowId { get; init; }
    public long? InstanceId { get; init; }
    public string? BusinessKey { get; init; }
    public int? NodeId { get; init; }
    public string? NodeExternalId { get; init; }
    public JsonElement? VariableFilter { get; init; }
}

/// <summary>Paged search request for version-change batch candidates.</summary>
public sealed record InstanceVersionChangeCandidateSearchRequest
{
    public InstanceVersionChangeCandidateFilterDto Filter { get; init; } = new();
    public bool? IncludeVariables { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>One running workflow instance eligible for batch selection.</summary>
public sealed record InstanceVersionChangeCandidateDto(
    long InstanceId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    string WorkflowName,
    int WorkflowVersion,
    string Status,
    string? BusinessKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExecutionPositionDto> ExecutionPositions)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Variables { get; init; }
}

public static class InstanceVersionChangeBatchSelectionModes
{
    public const string Explicit = "explicit";
    public const string AllMatching = "allMatching";
}

/// <summary>
/// A frozen candidate population expressed as explicit instance ids or as a
/// server-side filter snapshot plus exclusions.
/// </summary>
public sealed record InstanceVersionChangeBatchSelectionDto(
    string Mode,
    IReadOnlyList<long>? InstanceIds,
    InstanceVersionChangeCandidateFilterDto? Filter,
    IReadOnlyList<long>? ExcludedInstanceIds);

/// <summary>Creates and asynchronously prepares a version-change batch.</summary>
public sealed record CreateInstanceVersionChangeBatchRequest(
    long SourceWorkflowId,
    long TargetWorkflowId,
    string Reason,
    InstanceVersionChangeBatchSelectionDto Selection,
    string? IdempotencyKey);

/// <summary>
/// Confirms the exact prepared population using server-returned counts and an
/// optimistic batch timestamp.
/// </summary>
public sealed record ConfirmInstanceVersionChangeBatchRequest(
    int ExpectedEligibleItemCount,
    int ExpectedIneligibleItemCount,
    int ExpectedWarningItemCount,
    DateTimeOffset ExpectedBatchUpdatedAt);

public sealed record CancelInstanceVersionChangeBatchRequest(string? Reason);

/// <summary>Filters the durable version-change batch audit list.</summary>
public sealed record InstanceVersionChangeBatchSearchRequest
{
    public string? WorkflowKey { get; init; }
    public long? SourceWorkflowId { get; init; }
    public long? TargetWorkflowId { get; init; }
    public string? Status { get; init; }
    public string? PreparedBy { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>Aggregate lifecycle and progress for one durable batch.</summary>
public sealed record InstanceVersionChangeBatchSummaryDto(
    long Id,
    WorkflowSummaryDto SourceWorkflow,
    WorkflowSummaryDto TargetWorkflow,
    string Direction,
    string Reason,
    string Status,
    string PreparedBy,
    string? ConfirmedBy,
    int TotalItemCount,
    int EligibleItemCount,
    int WarningItemCount,
    int StaleItemCount,
    int BlockedItemCount,
    int IneligibleItemCount,
    int QueuedItemCount,
    int SucceededItemCount,
    int SkippedItemCount,
    int FailedItemCount,
    int CancelledItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Full frozen request, actor snapshots, and lifecycle metadata.</summary>
public sealed record InstanceVersionChangeBatchDetailDto(
    InstanceVersionChangeBatchSummaryDto Summary,
    JsonElement Selection,
    IReadOnlyList<string> PreparedByRoles,
    IReadOnlyList<string>? ConfirmedByRoles,
    JsonElement? Issues,
    long? PreparationJobId,
    long? ExecutionJobId,
    string? CancelledBy,
    string? CancellationReason,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CancelledAt);

/// <summary>Prepared compatibility and execution result for one instance.</summary>
public sealed record InstanceVersionChangeBatchItemDto(
    long Id,
    long BatchId,
    long InstanceId,
    string? BusinessKey,
    long CapturedSourceWorkflowId,
    DateTimeOffset CapturedInstanceUpdatedAt,
    string Status,
    IReadOnlyList<InstanceVersionChangeIssueDto> Blockers,
    IReadOnlyList<InstanceVersionChangeIssueDto> Warnings,
    JsonElement? Result,
    long? VersionChangeAuditId,
    string? ErrorCode,
    string? ErrorDescription,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
