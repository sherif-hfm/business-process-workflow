using System.Text.Json;

namespace Flowbit.Service.Models;

public static class InstanceVersionChangeBatchStatuses
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

public static class InstanceVersionChangeBatchItemStatuses
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

public static class InstanceVersionChangeBatchConstraints
{
    public const int MaxReasonLength = 1000;
    public const int MaxBatchInstances = 10_000;
    public const int MaxActorNameLength = 300;
    public const int MaxWorkflowKeyLength = 300;
    public const int MaxIdempotencyKeyLength = 300;
    public const int MaxErrorCodeLength = 100;
    public const int MaxErrorDescriptionLength = 1000;

    public const string MaxBatchInstancesSetting =
        "WorkflowVersionChanges.MaxBatchInstances";
}

public sealed record InstanceVersionChangeBatchJobPayload(long BatchId)
{
    public IReadOnlyDictionary<string, string>? ActorClaims { get; init; }
}

public sealed record InstanceVersionChangeBatchRecord(
    long Id,
    string WorkflowKey,
    long SourceWorkflowDefinitionId,
    long TargetWorkflowDefinitionId,
    string Reason,
    JsonElement Selection,
    string Status,
    string PreparedBy,
    IReadOnlyList<string> PreparedByRoles,
    string? ConfirmedBy,
    IReadOnlyList<string>? ConfirmedByRoles,
    int TotalItemCount,
    int EligibleItemCount,
    int IneligibleItemCount,
    int BlockedItemCount,
    int WarningItemCount,
    int StaleItemCount,
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

public sealed record NewInstanceVersionChangeBatchRecord(
    string WorkflowKey,
    long SourceWorkflowDefinitionId,
    long TargetWorkflowDefinitionId,
    string Reason,
    JsonElement Selection,
    string PreparedBy,
    IReadOnlyList<string> PreparedByRoles,
    string? IdempotencyKey,
    DateTimeOffset CreatedAt);

public sealed record InstanceVersionChangeBatchItemRecord(
    long Id,
    long BatchId,
    long InstanceId,
    string? BusinessKey,
    long CapturedSourceWorkflowDefinitionId,
    DateTimeOffset CapturedInstanceUpdatedAt,
    string Status,
    JsonElement? Blockers,
    JsonElement? Warnings,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorDescription,
    long? VersionChangeAuditId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record NewInstanceVersionChangeBatchItemRecord(
    long InstanceId,
    long CapturedSourceWorkflowDefinitionId,
    DateTimeOffset CapturedInstanceUpdatedAt,
    DateTimeOffset CreatedAt);

public sealed record InstanceVersionChangeBatchSearch(
    string? WorkflowKey,
    long? SourceWorkflowDefinitionId,
    long? TargetWorkflowDefinitionId,
    string? Status,
    string? PreparedBy,
    int Page,
    int PageSize);

public sealed record InstanceVersionChangeBatchUpdateRecord(
    long Id,
    string Status,
    string? ConfirmedBy,
    IReadOnlyList<string>? ConfirmedByRoles,
    int TotalItemCount,
    int EligibleItemCount,
    int IneligibleItemCount,
    int WarningItemCount,
    int StaleItemCount,
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

public sealed record InstanceVersionChangeBatchItemUpdateRecord(
    long Id,
    string Status,
    JsonElement? Blockers,
    JsonElement? Warnings,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorDescription,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
