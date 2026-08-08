using System.Text.Json;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Models;

public static class AdministrativeActionBatchStatuses
{
    public const string Preparing = "preparing";
    public const string Ready = "ready";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string CompletedWithIssues = "completedWithIssues";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    public static bool IsKnown(string value) => value is
        Preparing or Ready or Queued or Running or Completed
        or CompletedWithIssues or Cancelled or Failed;
}

public static class AdministrativeActionBatchItemStatuses
{
    public const string Preparing = "preparing";
    public const string Eligible = "eligible";
    public const string Ineligible = "ineligible";
    public const string Queued = "queued";
    public const string Succeeded = "succeeded";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsKnown(string value) => value is
        Preparing or Eligible or Ineligible or Queued or Succeeded
        or Skipped or Failed or Cancelled;
}

public static class AdministrativeActionConstraints
{
    public const int MaxReasonLength = 1000;
    public const int MaxAffectedTasks = 10_000;
    public const int MaxBatchItems = MaxAffectedTasks;
    public const int MaxActorNameLength = 300;
    public const int MaxWorkflowKeyLength = 300;
    public const int MaxIdempotencyKeyLength = 300;
    public const int MaxErrorCodeLength = 100;
    public const int MaxErrorDescriptionLength = 1000;

    public const string BatchMaxAffectedTasksSetting =
        "WorkflowBatchActions.MaxAffectedTasks";
}

public sealed record AdministrativeActionBatchJobPayload(long BatchId)
{
    public IReadOnlyDictionary<string, string>? ActorClaims { get; init; }
}

public sealed record AdministrativeActionSnapshotRecord(
    long WorkflowDefinitionId,
    int WorkflowVersion,
    string ActionKind,
    int FlowId,
    string? FlowExternalId,
    string FlowName,
    int SourceNodeId,
    string SourceNodeName,
    int TargetNodeId,
    string TargetNodeName,
    string TargetNodeType,
    string? Condition,
    IReadOnlyList<string> Roles,
    IReadOnlyList<VariableModel> Variables,
    int? BoundaryNodeId,
    string? BoundaryNodeName,
    TimerDefinitionModel? Timer,
    bool? AuthoredCancelActivity);

public sealed record AdministrativeActionBatchRecord(
    long Id,
    string WorkflowKey,
    long WorkflowDefinitionId,
    int SourceNodeId,
    string ActionKind,
    int FlowId,
    int? BoundaryNodeId,
    string? MultiInstanceMode,
    AdministrativeActionSnapshotRecord Action,
    string? Reason,
    IReadOnlyDictionary<string, JsonElement> CommonVariables,
    JsonElement Selection,
    string Status,
    string PreparedBy,
    IReadOnlyList<string> PreparedByRoles,
    string? ConfirmedBy,
    IReadOnlyList<string>? ConfirmedByRoles,
    int TotalItemCount,
    int TotalAffectedTaskCount,
    int EligibleItemCount,
    int IneligibleItemCount,
    int QueuedItemCount,
    int SucceededItemCount,
    int SkippedItemCount,
    int FailedItemCount,
    int CancelledItemCount,
    JsonElement? Issues,
    long? PreparationJobId,
    long? ExecutionJobId,
    string? IdempotencyKey,
    string? CancelledBy,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt);

public sealed record NewAdministrativeActionBatchRecord(
    string WorkflowKey,
    long WorkflowDefinitionId,
    int SourceNodeId,
    string ActionKind,
    int FlowId,
    int? BoundaryNodeId,
    string? MultiInstanceMode,
    AdministrativeActionSnapshotRecord Action,
    string? Reason,
    IReadOnlyDictionary<string, JsonElement> CommonVariables,
    JsonElement Selection,
    string PreparedBy,
    IReadOnlyList<string> PreparedByRoles,
    string? IdempotencyKey,
    DateTimeOffset CreatedAt);

public sealed record AdministrativeActionBatchItemRecord(
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

public sealed record NewAdministrativeActionBatchItemRecord(
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
    DateTimeOffset CreatedAt);

public sealed record AdministrativeActionBatchSearch(
    string? WorkflowKey,
    long? WorkflowDefinitionId,
    string? Status,
    string? PreparedBy,
    int Page,
    int PageSize);

public sealed record AdministrativeActionBatchUpdateRecord(
    long Id,
    string Status,
    string? ConfirmedBy,
    IReadOnlyList<string>? ConfirmedByRoles,
    int TotalItemCount,
    int TotalAffectedTaskCount,
    int EligibleItemCount,
    int IneligibleItemCount,
    int QueuedItemCount,
    int SucceededItemCount,
    int SkippedItemCount,
    int FailedItemCount,
    int CancelledItemCount,
    JsonElement? Issues,
    long? PreparationJobId,
    long? ExecutionJobId,
    string? CancelledBy,
    string? CancellationReason,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt);

public sealed record AdministrativeActionBatchItemUpdateRecord(
    long Id,
    string Status,
    int AffectedTaskCount,
    JsonElement? Issues,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorDescription,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
