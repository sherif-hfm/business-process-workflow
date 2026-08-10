using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class InstanceVariableUpdateAuditEntity
{
    public long Id { get; set; }

    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public long WorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }

    public string PerformedBy { get; set; } = string.Empty;

    public JsonDocument PerformedByRolesJson { get; set; } = JsonDocument.Parse("[]");

    public string? Reason { get; set; }

    public JsonDocument RequestedVariablesJson { get; set; } = JsonDocument.Parse("[]");

    public JsonDocument ResultJson { get; set; } = JsonDocument.Parse("{}");

    public string? IdempotencyKey { get; set; }

    public long? BatchId { get; set; }

    public InstanceVariableUpdateBatchEntity? Batch { get; set; }

    public long? BatchItemId { get; set; }

    public InstanceVariableUpdateBatchItemEntity? BatchItem { get; set; }

    public DateTimeOffset PerformedAt { get; set; }

    public List<InstanceVariableEntity> Variables { get; set; } = [];
}
