using System.Text.Json;

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
    public const int MaxBatchItems = 10_000;
    public const int MaxActorNameLength = 300;
    public const int MaxWorkflowKeyLength = 300;
    public const int MaxExternalIdLength = 300;
    public const int MaxIdempotencyKeyLength = 300;
    public const int MaxErrorCodeLength = 100;
    public const int MaxErrorDescriptionLength = 1000;

    public const string AdministrativeRequiredRoleSetting =
        "WorkflowAdministrativeActions.RequiredRole";
    public const string BatchRequiredRoleSetting =
        "WorkflowBatchActions.RequiredRole";
    public const string BatchMaxItemsSetting =
        "WorkflowBatchActions.MaxItems";
    public const string DefaultRequiredRole = "admin";
}

public sealed record AdministrativeActionBatchJobPayload(long BatchId)
{
    /// <summary>
    /// Snapshot of only the deployment-allowlisted claims needed to reproduce
    /// sys.claim.* evaluation in the durable worker.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ActorClaims { get; init; }
}

public sealed record AdministrativeActionBatchRecord(
    long Id,
    long TargetWorkflowDefinitionId,
    string WorkflowKey,
    string FlowExternalId,
    string Reason,
    IReadOnlyDictionary<string, JsonElement> CommonVariables,
    JsonElement Selection,
    string Status,
    string PreparedBy,
    IReadOnlyList<string> PreparedByRoles,
    string? ConfirmedBy,
    IReadOnlyList<string>? ConfirmedByRoles,
    int TotalItemCount,
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
    long TargetWorkflowDefinitionId,
    string WorkflowKey,
    string FlowExternalId,
    string Reason,
    IReadOnlyDictionary<string, JsonElement> CommonVariables,
    JsonElement Selection,
    string PreparedBy,
    IReadOnlyList<string> PreparedByRoles,
    string? IdempotencyKey,
    DateTimeOffset CreatedAt);

public sealed record AdministrativeActionBatchItemRecord(
    long Id,
    long BatchId,
    long InstanceId,
    long UserTaskId,
    long TokenId,
    long SourceWorkflowDefinitionId,
    long TargetWorkflowDefinitionId,
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

public sealed record NewAdministrativeActionBatchItemRecord(
    long InstanceId,
    long UserTaskId,
    long TokenId,
    long SourceWorkflowDefinitionId,
    long TargetWorkflowDefinitionId,
    DateTimeOffset CapturedInstanceUpdatedAt,
    DateTimeOffset CapturedUserTaskUpdatedAt,
    DateTimeOffset CreatedAt);

public sealed record AdministrativeActionBatchSearch(
    string? WorkflowKey,
    string? Status,
    string? PreparedBy,
    int Page,
    int PageSize);

public sealed record AdministrativeActionBatchListAuthorization(
    IReadOnlyList<string> LowerActorRoles);

public sealed record AdministrativeActionBatchUpdateRecord(
    long Id,
    string Status,
    string? ConfirmedBy,
    IReadOnlyList<string>? ConfirmedByRoles,
    int TotalItemCount,
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
    JsonElement? Issues,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorDescription,
    long? NewUserTaskId,
    long? VersionChangeAuditId,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
