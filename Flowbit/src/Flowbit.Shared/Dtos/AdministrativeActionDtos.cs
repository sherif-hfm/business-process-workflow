using System.Text.Json;
using Flowbit.Shared.Models;

namespace Flowbit.Shared.Dtos;

public sealed record AdministrativeActionSummaryDto(
    int FlowId,
    string FlowExternalId,
    string Name,
    int SourceNodeId,
    string SourceNodeName,
    int TargetNodeId,
    string TargetNodeName,
    bool IsBatchable,
    IReadOnlyList<VariableModel> Variables);

/// <summary>
/// Minimal privileged task context used when an administrative operator is
/// intentionally not authorized for the task's ordinary work-item view.
/// </summary>
public sealed record AdministrativeActionTaskContextDto(
    long UserTaskId,
    long InstanceId,
    long TokenId,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    long SourceWorkflowId,
    string WorkflowKey,
    string WorkflowName,
    int SourceWorkflowVersion,
    DateTimeOffset InstanceUpdatedAt,
    DateTimeOffset UserTaskUpdatedAt,
    IReadOnlyList<WorkflowSummaryDto> TargetVersions);

public sealed record AdministrativeActionRequest(
    long TargetWorkflowId,
    long ExpectedSourceWorkflowId,
    DateTimeOffset ExpectedInstanceUpdatedAt,
    string FlowExternalId,
    string Reason,
    Dictionary<string, JsonElement>? Variables)
{
    public long? ExpectedTokenId { get; init; }
    public DateTimeOffset? ExpectedUserTaskUpdatedAt { get; init; }
}

public sealed record AdministrativeActionResultDto(
    InstanceDetailDto Instance,
    long CompletedUserTaskId,
    UserTaskDto? NewUserTask,
    InstanceVersionChangeAuditDto? VersionChange,
    long? AdministrativeActionBatchId);

public sealed record AdministrativeActionEligibilityDto(
    bool Eligible,
    IReadOnlyList<InstanceVersionChangeIssueDto> Issues);

public sealed record AdministrativeActionCandidateSearchRequest
{
    public long TargetWorkflowId { get; init; }
    public string FlowExternalId { get; init; } = string.Empty;
    public long? UserTaskId { get; init; }
    public long? InstanceId { get; init; }
    public long? SourceWorkflowId { get; init; }
    public string? BusinessKey { get; init; }
    public JsonElement? VariableFilter { get; init; }
    public bool? IncludeVariables { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

public sealed record AdministrativeActionCandidateDto(
    long UserTaskId,
    long InstanceId,
    long TokenId,
    long SourceWorkflowId,
    string WorkflowKey,
    string? BusinessKey,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    DateTimeOffset InstanceUpdatedAt,
    DateTimeOffset UserTaskUpdatedAt,
    bool Eligible,
    IReadOnlyList<InstanceVersionChangeIssueDto> Issues)
{
    public IReadOnlyDictionary<string, JsonElement>? Variables { get; init; }
}

public static class AdministrativeActionBatchSelectionModes
{
    public const string Explicit = "explicit";
    public const string AllMatching = "allMatching";
}

public sealed record AdministrativeActionBatchSelectionDto(
    string Mode,
    IReadOnlyList<long>? UserTaskIds,
    AdministrativeActionCandidateSearchRequest? AllMatching,
    IReadOnlyList<long>? ExcludedUserTaskIds);

public sealed record CreateAdministrativeActionBatchRequest(
    long TargetWorkflowId,
    string FlowExternalId,
    string Reason,
    Dictionary<string, JsonElement>? Variables,
    AdministrativeActionBatchSelectionDto Selection,
    string? IdempotencyKey);

public sealed record ConfirmAdministrativeActionBatchRequest(
    int ExpectedEligibleItemCount,
    DateTimeOffset ExpectedBatchUpdatedAt);

public sealed record CancelAdministrativeActionBatchRequest(string? Reason);

public sealed record AdministrativeActionBatchSearchRequest
{
    public string? WorkflowKey { get; init; }
    public string? Status { get; init; }
    public string? PreparedBy { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

public sealed record AdministrativeActionBatchSummaryDto(
    long Id,
    long TargetWorkflowId,
    string WorkflowKey,
    string FlowExternalId,
    string Reason,
    string Status,
    string PreparedBy,
    string? ConfirmedBy,
    int TotalItemCount,
    int EligibleItemCount,
    int IneligibleItemCount,
    int QueuedItemCount,
    int SucceededItemCount,
    int SkippedItemCount,
    int FailedItemCount,
    int CancelledItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record AdministrativeActionBatchDetailDto(
    AdministrativeActionBatchSummaryDto Summary,
    IReadOnlyDictionary<string, JsonElement> CommonVariables,
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

public sealed record AdministrativeActionBatchItemDto(
    long Id,
    long BatchId,
    long InstanceId,
    long UserTaskId,
    long TokenId,
    long SourceWorkflowId,
    long TargetWorkflowId,
    DateTimeOffset CapturedInstanceUpdatedAt,
    DateTimeOffset CapturedUserTaskUpdatedAt,
    string Status,
    JsonElement? Issues,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorDescription,
    long? NewUserTaskId,
    long? VersionChangeAuditId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
