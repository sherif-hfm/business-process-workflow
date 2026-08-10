using System.Text.Json;

namespace Flowbit.Service.Models;

public static class InstanceVariableUpdateOutcomes
{
    public const string Added = "added";
    public const string Updated = "updated";
}

public static class InstanceVariableUpdateBatchStatuses
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

public static class InstanceVariableUpdateBatchItemStatuses
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

public static class InstanceVariableUpdateBatchPhases
{
    public const string Prepare = "prepare";
    public const string Execute = "execute";
}

public static class InstanceVariableUpdateConstraints
{
    public const int MaxVariables = 100;
    public const int MaxVariableNameLength = 300;
    public const int MaxReasonLength = 1000;
    public const int MaxActorNameLength = 300;
    public const int MaxWorkflowKeyLength = 300;
    public const int MaxIdempotencyKeyLength = 300;
    public const int MaxErrorCodeLength = 100;
    public const int MaxErrorDescriptionLength = 1000;
    public const int MaxBatchInstances = 10_000;
    public const int MaxExpandedWrites = 100_000;
    public const long MaxExpandedPayloadBytes = 100L * 1024 * 1024;
    public const string MaxBatchInstancesSetting =
        "WorkflowVariableUpdates.MaxBatchInstances";
}

public sealed record InstanceVariableUpdateBatchJobPayload(
    long BatchId,
    long WorkflowDefinitionId,
    string Phase)
{
    public IReadOnlyDictionary<string, string>? ActorClaims { get; init; }
}

public sealed record InstanceVariableUpdateCandidateQuery(
    string WorkflowKey,
    long? WorkflowDefinitionId,
    long? InstanceId,
    string? BusinessKey,
    int? NodeId,
    string? NodeExternalId,
    VariableFilterExpression? VariableFilter,
    IReadOnlyList<InstanceSortCriterion> Sort,
    string? Cursor,
    bool IncludeVariables,
    int Page,
    int PageSize,
    IReadOnlyList<long>? InstanceIds = null);

public sealed record FrozenInstanceVariableUpdateCandidate(
    long InstanceId,
    long WorkflowDefinitionId,
    string? BusinessKey,
    DateTimeOffset UpdatedAt);

public sealed record InstanceVariableUpdateWriteRecord(
    string Name,
    string Outcome,
    JsonElement Value);

public sealed record InstanceVariableUpdateVariableRecord(
    long Id,
    string Name,
    JsonElement Value);

public sealed record InstanceVariableUpdateAuditRecord(
    long Id,
    long InstanceId,
    long WorkflowDefinitionId,
    string PerformedBy,
    IReadOnlyList<string> PerformedByRoles,
    string? Reason,
    JsonElement RequestedVariables,
    JsonElement Result,
    string? IdempotencyKey,
    long? BatchId,
    long? BatchItemId,
    DateTimeOffset PerformedAt);

public sealed record NewInstanceVariableUpdateAuditRecord(
    long InstanceId,
    long WorkflowDefinitionId,
    string PerformedBy,
    IReadOnlyList<string> PerformedByRoles,
    string? Reason,
    JsonElement RequestedVariables,
    string? IdempotencyKey,
    long? BatchId,
    long? BatchItemId,
    DateTimeOffset PerformedAt);

public sealed record InstanceVariableUpdateBatchRecord(
    long Id,
    string WorkflowKey,
    JsonElement Variables,
    JsonElement Selection,
    string? Reason,
    string Status,
    string PreparedBy,
    IReadOnlyList<string> PreparedByRoles,
    string? ConfirmedBy,
    IReadOnlyList<string>? ConfirmedByRoles,
    int TotalItemCount,
    int EligibleItemCount,
    int IneligibleItemCount,
    int WarningItemCount,
    int QueuedItemCount,
    int SucceededItemCount,
    int SkippedItemCount,
    int FailedItemCount,
    int CancelledItemCount,
    JsonElement? Issues,
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

public sealed record NewInstanceVariableUpdateBatchRecord(
    string WorkflowKey,
    JsonElement Variables,
    JsonElement Selection,
    string? Reason,
    string PreparedBy,
    IReadOnlyList<string> PreparedByRoles,
    string? IdempotencyKey,
    DateTimeOffset CreatedAt);

public sealed record InstanceVariableUpdateBatchUpdateRecord(
    long Id,
    string Status,
    string? ConfirmedBy,
    IReadOnlyList<string>? ConfirmedByRoles,
    int TotalItemCount,
    int EligibleItemCount,
    int IneligibleItemCount,
    int WarningItemCount,
    int QueuedItemCount,
    int SucceededItemCount,
    int SkippedItemCount,
    int FailedItemCount,
    int CancelledItemCount,
    JsonElement? Issues,
    string? CancelledBy,
    string? CancellationReason,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt);

public sealed record InstanceVariableUpdateBatchItemRecord(
    long Id,
    long BatchId,
    long InstanceId,
    string? BusinessKey,
    long CapturedWorkflowDefinitionId,
    DateTimeOffset CapturedInstanceUpdatedAt,
    string Status,
    JsonElement? Plan,
    JsonElement? Warnings,
    JsonElement? Result,
    long? UpdateOperationId,
    string? ErrorCode,
    string? ErrorDescription,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record NewInstanceVariableUpdateBatchItemRecord(
    long InstanceId,
    long CapturedWorkflowDefinitionId,
    DateTimeOffset CapturedInstanceUpdatedAt,
    DateTimeOffset CreatedAt);

public sealed record InstanceVariableUpdateBatchItemUpdateRecord(
    long Id,
    string Status,
    JsonElement? Plan,
    JsonElement? Warnings,
    JsonElement? Result,
    long? UpdateOperationId,
    string? ErrorCode,
    string? ErrorDescription,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record InstanceVariableUpdateBatchJobLinkRecord(
    long Id,
    long BatchId,
    long WorkflowDefinitionId,
    string Phase,
    long OriginalJobId,
    long? JobId);

public sealed record NewInstanceVariableUpdateBatchJobLinkRecord(
    long BatchId,
    long WorkflowDefinitionId,
    string Phase,
    long OriginalJobId,
    long? JobId);

public sealed record InstanceVariableUpdateBatchSearch(
    string? WorkflowKey,
    string? Status,
    string? PreparedBy,
    int Page,
    int PageSize);
