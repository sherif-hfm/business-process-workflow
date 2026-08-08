using System.Text.Json;
using Flowbit.Shared.Models;

namespace Flowbit.Shared.Dtos;

public static class AdministrativeActionKinds
{
    public const string DirectFlow = "directFlow";
    public const string TimerBoundary = "timerBoundary";

    public static bool IsKnown(string? value) => value is DirectFlow or TimerBoundary;
}

public static class AdministrativeActionPositionKinds
{
    public const string UserTask = "userTask";
    public const string MultiInstanceExecution = "multiInstanceExecution";

    public static bool IsKnown(string? value) => value is UserTask or MultiInstanceExecution;
}

public static class AdministrativeActionMultiInstanceModes
{
    public const string ForceParent = "forceParent";
    public const string CompleteAllChildren = "completeAllChildren";

    public static bool IsKnown(string? value) => value is ForceParent or CompleteAllChildren;
}

public sealed record AdministrativeActionPositionReferenceDto(
    string PositionKind,
    long PositionId);

public sealed record AdministrativeActionSourceNodeDto(
    long WorkflowDefinitionId,
    int WorkflowVersion,
    int NodeId,
    string Name,
    string? ExternalId,
    bool IsMultiInstance);

public sealed record AdministrativeActionSummaryDto(
    long WorkflowDefinitionId,
    int WorkflowVersion,
    string ActionKind,
    int FlowId,
    string? FlowExternalId,
    string Name,
    int SourceNodeId,
    string SourceNodeName,
    int TargetNodeId,
    string TargetNodeName,
    string TargetNodeType,
    IReadOnlyList<VariableModel> Variables)
{
    public string? Condition { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public int? BoundaryNodeId { get; init; }
    public string? BoundaryNodeName { get; init; }
    public TimerDefinitionModel? Timer { get; init; }
    public bool? AuthoredCancelActivity { get; init; }
}

public sealed record AdministrativeTimerBoundaryStateDto(
    int BoundaryNodeId,
    long? TimerSubscriptionId,
    long? TimerJobId,
    string? Status,
    DateTimeOffset? NextDueAt,
    long? Occurrence,
    DateTimeOffset? UpdatedAt,
    bool Eligible);

/// <summary>
/// Internal request used by the durable worker for one frozen execution
/// position. No public single-position administrative endpoint is exposed.
/// </summary>
public sealed record AdministrativeActionRequest
{
    public required long BatchId { get; init; }
    public required long BatchItemId { get; init; }
    public required long ExpectedWorkflowDefinitionId { get; init; }
    public required int SourceNodeId { get; init; }
    public required string ActionKind { get; init; }
    public required int FlowId { get; init; }
    public int? BoundaryNodeId { get; init; }
    public string? MultiInstanceMode { get; init; }
    public required string PositionKind { get; init; }
    public required long PositionId { get; init; }
    public long? UserTaskId { get; init; }
    public long? MultiInstanceExecutionId { get; init; }
    public required long ExpectedTokenId { get; init; }
    public required Guid ExpectedTokenActivationId { get; init; }
    public required DateTimeOffset ExpectedPositionUpdatedAt { get; init; }
    public long? ExpectedTimerSubscriptionId { get; init; }
    public long? ExpectedTimerJobId { get; init; }
    public long? ExpectedTimerOccurrence { get; init; }
    public string? ExpectedTimerStatus { get; init; }
    public DateTimeOffset? ExpectedTimerSubscriptionUpdatedAt { get; init; }
    public string? Reason { get; init; }
    public Dictionary<string, JsonElement>? Variables { get; init; }
}

public sealed record AdministrativeActionResultDto(
    InstanceDetailDto Instance,
    string PositionKind,
    long PositionId,
    int AffectedTaskCount,
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
    int AffectedTaskCount,
    IReadOnlyList<AdministrativeActionIssueDto> Issues);

public sealed record AdministrativeActionCandidateSearchRequest
{
    public long WorkflowDefinitionId { get; init; }
    public int SourceNodeId { get; init; }
    public string? PositionKind { get; init; }
    public long? PositionId { get; init; }
    public long? InstanceId { get; init; }
    public string? BusinessKey { get; init; }
    public JsonElement? VariableFilter { get; init; }
    public IReadOnlyList<AdministrativeActionPositionReferenceDto>? ExcludedPositions { get; init; }
    public bool? IncludeVariables { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

public sealed record AdministrativeActionCandidateDto(
    string PositionKind,
    long PositionId,
    long? UserTaskId,
    long? MultiInstanceExecutionId,
    long InstanceId,
    long TokenId,
    Guid TokenActivationId,
    long WorkflowDefinitionId,
    int WorkflowVersion,
    string WorkflowKey,
    string? BusinessKey,
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    DateTimeOffset PositionUpdatedAt,
    int AffectedTaskCount,
    IReadOnlyList<AdministrativeTimerBoundaryStateDto> TimerBoundaries)
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
    IReadOnlyList<AdministrativeActionPositionReferenceDto>? Positions,
    AdministrativeActionCandidateSearchRequest? AllMatching,
    IReadOnlyList<AdministrativeActionPositionReferenceDto>? ExcludedPositions);

public sealed record CreateAdministrativeActionBatchRequest(
    long WorkflowDefinitionId,
    int SourceNodeId,
    string ActionKind,
    int FlowId,
    int? BoundaryNodeId,
    string? MultiInstanceMode,
    string? Reason,
    Dictionary<string, JsonElement>? Variables,
    AdministrativeActionBatchSelectionDto Selection,
    string? IdempotencyKey);

public sealed record ConfirmAdministrativeActionBatchRequest(
    int ExpectedEligibleItemCount,
    int ExpectedAffectedTaskCount,
    DateTimeOffset ExpectedBatchUpdatedAt);

public sealed record CancelAdministrativeActionBatchRequest(string? Reason);

public sealed record AdministrativeActionBatchSearchRequest
{
    public string? WorkflowKey { get; init; }
    public long? WorkflowDefinitionId { get; init; }
    public string? Status { get; init; }
    public string? PreparedBy { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

public sealed record AdministrativeActionBatchSummaryDto(
    long Id,
    string WorkflowKey,
    long WorkflowDefinitionId,
    int WorkflowVersion,
    int SourceNodeId,
    string SourceNodeName,
    string ActionKind,
    int FlowId,
    int? BoundaryNodeId,
    string? MultiInstanceMode,
    string? Reason,
    string Status,
    string PreparedBy,
    string? ConfirmedBy,
    int TotalItemCount,
    int TotalAffectedTaskCount,
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
    AdministrativeActionSummaryDto Action,
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
    string PositionKind,
    long PositionId,
    long InstanceId,
    long? UserTaskId,
    long? MultiInstanceExecutionId,
    long TokenId,
    Guid TokenActivationId,
    long WorkflowDefinitionId,
    int SourceNodeId,
    int FlowId,
    DateTimeOffset CapturedPositionUpdatedAt,
    long? TimerSubscriptionId,
    long? TimerJobId,
    long? CapturedTimerOccurrence,
    string? CapturedTimerStatus,
    DateTimeOffset? CapturedTimerSubscriptionUpdatedAt,
    int AffectedTaskCount,
    string Status,
    JsonElement? Issues,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorDescription,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
