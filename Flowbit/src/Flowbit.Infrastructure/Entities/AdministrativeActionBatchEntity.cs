using System.Text.Json;
using Flowbit.Service.Models;

namespace Flowbit.Infrastructure.Entities;

public sealed class AdministrativeActionBatchEntity
{
    public long Id { get; set; }

    public string WorkflowKey { get; set; } = string.Empty;

    public JsonDocument FlowMappingsJson { get; set; } = JsonDocument.Parse("[]");

    public string Reason { get; set; } = string.Empty;

    public JsonDocument CommonVariablesJson { get; set; } = JsonDocument.Parse("{}");

    public JsonDocument SelectionJson { get; set; } = JsonDocument.Parse("{}");

    public string Status { get; set; } = AdministrativeActionBatchStatuses.Preparing;

    public string PreparedBy { get; set; } = string.Empty;

    public JsonDocument PreparedByRolesJson { get; set; } = JsonDocument.Parse("[]");

    public string? ConfirmedBy { get; set; }

    public JsonDocument? ConfirmedByRolesJson { get; set; }

    public int TotalItemCount { get; set; }

    public int EligibleItemCount { get; set; }

    public int IneligibleItemCount { get; set; }

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

    public List<AdministrativeActionBatchItemEntity> Items { get; set; } = [];

    public List<UserTaskEntity> CompletedUserTasks { get; set; } = [];

    public List<InstanceHistoryEntity> InstanceHistory { get; set; } = [];
}
