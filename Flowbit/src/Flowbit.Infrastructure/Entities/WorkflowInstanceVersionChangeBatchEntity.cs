using System.Text.Json;
using Flowbit.Service.Models;

namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowInstanceVersionChangeBatchEntity
{
    public long Id { get; set; }

    public string WorkflowKey { get; set; } = string.Empty;

    public long SourceWorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? SourceWorkflowDefinition { get; set; }

    public long TargetWorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? TargetWorkflowDefinition { get; set; }

    public string Reason { get; set; } = string.Empty;

    public JsonDocument SelectionJson { get; set; } = JsonDocument.Parse("{}");

    public string Status { get; set; } = InstanceVersionChangeBatchStatuses.Preparing;

    public string PreparedBy { get; set; } = string.Empty;

    public JsonDocument PreparedByRolesJson { get; set; } = JsonDocument.Parse("[]");

    public string? ConfirmedBy { get; set; }

    public JsonDocument? ConfirmedByRolesJson { get; set; }

    public int TotalItemCount { get; set; }

    public int EligibleItemCount { get; set; }

    public int IneligibleItemCount { get; set; }

    public int BlockedItemCount { get; set; }

    public int WarningItemCount { get; set; }

    public int StaleItemCount { get; set; }

    public int QueuedItemCount { get; set; }

    public int SucceededItemCount { get; set; }

    public int SkippedItemCount { get; set; }

    public int FailedItemCount { get; set; }

    public int CancelledItemCount { get; set; }

    public JsonDocument? IssuesJson { get; set; }

    public long? PreparationJobId { get; set; }

    public WorkflowJobEntity? PreparationJob { get; set; }

    public long? ExecutionJobId { get; set; }

    public WorkflowJobEntity? ExecutionJob { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? CancelledBy { get; set; }

    public string? CancellationReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PreparedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public List<WorkflowInstanceVersionChangeBatchItemEntity> Items { get; set; } = [];

    public List<WorkflowInstanceVersionChangeEntity> VersionChanges { get; set; } = [];
}
