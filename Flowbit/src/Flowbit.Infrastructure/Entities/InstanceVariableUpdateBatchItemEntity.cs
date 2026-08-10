using System.Text.Json;
using Flowbit.Service.Models;

namespace Flowbit.Infrastructure.Entities;

public sealed class InstanceVariableUpdateBatchItemEntity
{
    public long Id { get; set; }

    public long BatchId { get; set; }

    public InstanceVariableUpdateBatchEntity? Batch { get; set; }

    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public long CapturedWorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? CapturedWorkflowDefinition { get; set; }

    public DateTimeOffset CapturedInstanceUpdatedAt { get; set; }

    public string Status { get; set; } = InstanceVariableUpdateBatchItemStatuses.Preparing;

    public JsonDocument? PlanJson { get; set; }

    public JsonDocument? WarningsJson { get; set; }

    public JsonDocument? ResultJson { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorDescription { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PreparedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
