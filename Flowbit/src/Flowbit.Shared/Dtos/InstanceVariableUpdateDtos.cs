using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flowbit.Shared.Dtos;

/// <summary>One raw JSON variable value to append to an instance.</summary>
public sealed record InstanceVariableWriteDto(string Name, JsonElement Value);

/// <summary>Atomically updates one or more variables on one running instance.</summary>
public sealed record UpdateInstanceVariablesRequest(
    IReadOnlyList<InstanceVariableWriteDto> Variables,
    string? Reason,
    string? IdempotencyKey);

public sealed record InstanceVariableUpdateIssueDto(string Code, string Message);

/// <summary>The actual result of one append-only variable write.</summary>
public sealed record InstanceVariableUpdateOutcomeDto(
    string Name,
    string Outcome,
    long VariableId,
    JsonElement Value);

/// <summary>Result of one atomic administrative instance-variable operation.</summary>
public sealed record UpdateInstanceVariablesResultDto(
    long OperationId,
    long InstanceId,
    long WorkflowDefinitionId,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<InstanceVariableUpdateOutcomeDto> Variables,
    IReadOnlyList<InstanceVariableUpdateIssueDto> Warnings)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BatchId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BatchItemId { get; init; }
}

/// <summary>Immutable audit for a successful administrative variable update.</summary>
public sealed record InstanceVariableUpdateAuditDto(
    long Id,
    long InstanceId,
    long WorkflowDefinitionId,
    string PerformedBy,
    IReadOnlyList<string> PerformedByRoles,
    string? Reason,
    IReadOnlyList<InstanceVariableUpdateOutcomeDto> Variables,
    DateTimeOffset PerformedAt,
    string? IdempotencyKey,
    long? BatchId,
    long? BatchItemId);

/// <summary>Server-authoritative filters for variable-update candidates.</summary>
public sealed record InstanceVariableUpdateCandidateFilterDto
{
    public string WorkflowKey { get; init; } = string.Empty;
    public long? WorkflowId { get; init; }
    public long? InstanceId { get; init; }
    public string? BusinessKey { get; init; }
    public int? NodeId { get; init; }
    public string? NodeExternalId { get; init; }
    public JsonElement? VariableFilter { get; init; }
}

public sealed record InstanceVariableUpdateCandidateSearchRequest
{
    public InstanceVariableUpdateCandidateFilterDto Filter { get; init; } = new();
    public IReadOnlyList<SearchSortDto>? Sort { get; init; }
    public string? Cursor { get; init; }
    public bool? IncludeVariables { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>One running instance available to a variable-update batch.</summary>
public sealed record InstanceVariableUpdateCandidateDto(
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstanceJobSummaryDto? Jobs { get; init; }
}

public static class InstanceVariableUpdateBatchSelectionModes
{
    public const string Explicit = "explicit";
    public const string AllMatching = "allMatching";
}

public sealed record InstanceVariableUpdateBatchSelectionDto(
    string Mode,
    IReadOnlyList<long>? InstanceIds,
    InstanceVariableUpdateCandidateFilterDto? Filter,
    IReadOnlyList<long>? ExcludedInstanceIds);

/// <summary>Freezes and asynchronously prepares a variable-update batch.</summary>
public sealed record CreateInstanceVariableUpdateBatchRequest(
    string WorkflowKey,
    IReadOnlyList<InstanceVariableWriteDto> Variables,
    string? Reason,
    InstanceVariableUpdateBatchSelectionDto Selection,
    string? IdempotencyKey);

public sealed record ConfirmInstanceVariableUpdateBatchRequest(
    int ExpectedEligibleItemCount,
    int ExpectedIneligibleItemCount,
    int ExpectedWarningItemCount,
    DateTimeOffset ExpectedBatchUpdatedAt);

public sealed record CancelInstanceVariableUpdateBatchRequest(string? Reason);

public sealed record InstanceVariableUpdateBatchSearchRequest
{
    public string? WorkflowKey { get; init; }
    public string? Status { get; init; }
    public string? PreparedBy { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

public sealed record InstanceVariableUpdateBatchSummaryDto(
    long Id,
    string WorkflowKey,
    string Status,
    string PreparedBy,
    string? ConfirmedBy,
    string? Reason,
    int VariableCount,
    int WorkflowDefinitionCount,
    int TotalItemCount,
    int EligibleItemCount,
    int IneligibleItemCount,
    int WarningItemCount,
    int QueuedItemCount,
    int SucceededItemCount,
    int SkippedItemCount,
    int FailedItemCount,
    int CancelledItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>A durable prepare/execute job associated with one workflow version.</summary>
public sealed record InstanceVariableUpdateBatchJobLinkDto(
    long Id,
    long OriginalJobId,
    long? JobId,
    string Phase,
    WorkflowSummaryDto Workflow,
    string? JobStatus);

public sealed record InstanceVariableUpdateBatchDetailDto(
    InstanceVariableUpdateBatchSummaryDto Summary,
    JsonElement Selection,
    IReadOnlyList<InstanceVariableWriteDto> Variables,
    IReadOnlyList<string> PreparedByRoles,
    IReadOnlyList<string>? ConfirmedByRoles,
    IReadOnlyList<InstanceVariableUpdateIssueDto> Issues,
    IReadOnlyList<InstanceVariableUpdateBatchJobLinkDto> Jobs,
    string? CancelledBy,
    string? CancellationReason,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CancelledAt);

/// <summary>Prepared plan and eventual result for one frozen instance.</summary>
public sealed record InstanceVariableUpdateBatchItemDto(
    long Id,
    long BatchId,
    long InstanceId,
    string? BusinessKey,
    long CapturedWorkflowDefinitionId,
    DateTimeOffset CapturedInstanceUpdatedAt,
    string Status,
    IReadOnlyList<InstanceVariableUpdateOutcomePlanDto> Plan,
    IReadOnlyList<InstanceVariableUpdateIssueDto> Warnings,
    JsonElement? Result,
    long? UpdateOperationId,
    string? ErrorCode,
    string? ErrorDescription,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record InstanceVariableUpdateOutcomePlanDto(string Name, string Outcome);
