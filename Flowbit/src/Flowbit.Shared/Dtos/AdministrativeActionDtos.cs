using System.Text.Json;
using Flowbit.Shared.Models;

namespace Flowbit.Shared.Dtos;

public sealed record AdministrativeActionFlowMappingDto(
    long WorkflowDefinitionId,
    int FlowId);

public sealed record AdministrativeActionSummaryDto(
    long WorkflowDefinitionId,
    int WorkflowVersion,
    int FlowId,
    string? FlowExternalId,
    string Name,
    int SourceNodeId,
    string SourceNodeName,
    int TargetNodeId,
    string TargetNodeName,
    IReadOnlyList<VariableModel> Variables);

public sealed record AdministrativeActionFlowMappingSnapshotDto(
    long WorkflowDefinitionId,
    int WorkflowVersion,
    int FlowId,
    string? FlowExternalId,
    string Name,
    int SourceNodeId,
    string SourceNodeName,
    int TargetNodeId,
    string TargetNodeName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<VariableModel> Variables);

/// <summary>
/// Internal execution request for one frozen administrative batch item. This is
/// deliberately not exposed by a single-task endpoint: ordinary single actions
/// continue to use the normal user-task API.
/// </summary>
public sealed record AdministrativeActionRequest(
    long ExpectedWorkflowDefinitionId,
    int FlowId,
    DateTimeOffset ExpectedInstanceUpdatedAt,
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
    long AdministrativeActionBatchId);

public sealed record AdministrativeActionIssueDto(
    string Code,
    string Message,
    string? StateType = null,
    long? StateId = null,
    int? NodeId = null,
    int? FlowId = null);

public sealed record AdministrativeActionEligibilityDto(
    bool Eligible,
    IReadOnlyList<AdministrativeActionIssueDto> Issues);

public sealed record AdministrativeActionCandidateSearchRequest
{
    public IReadOnlyList<AdministrativeActionFlowMappingDto> FlowMappings { get; init; } = [];
    public long? UserTaskId { get; init; }
    public long? InstanceId { get; init; }
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
    long WorkflowDefinitionId,
    int WorkflowVersion,
    int FlowId,
    string FlowName,
    string WorkflowKey,
    string? BusinessKey,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    DateTimeOffset InstanceUpdatedAt,
    DateTimeOffset UserTaskUpdatedAt,
    bool Eligible,
    IReadOnlyList<AdministrativeActionIssueDto> Issues)
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
    IReadOnlyList<AdministrativeActionFlowMappingDto> FlowMappings,
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
    string WorkflowKey,
    int FlowMappingCount,
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
    IReadOnlyList<AdministrativeActionFlowMappingSnapshotDto> FlowMappings,
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
    long WorkflowDefinitionId,
    int FlowId,
    DateTimeOffset CapturedInstanceUpdatedAt,
    DateTimeOffset CapturedUserTaskUpdatedAt,
    string Status,
    JsonElement? Issues,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorDescription,
    long? NewUserTaskId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
