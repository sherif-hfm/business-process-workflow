using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowInstanceVersionChangeEntity
{
    public long Id { get; set; }

    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public long SourceWorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? SourceWorkflowDefinition { get; set; }

    public long TargetWorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? TargetWorkflowDefinition { get; set; }

    public string? ChangedBy { get; set; }

    public JsonDocument ChangedByRolesJson { get; set; } = JsonDocument.Parse("[]");

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
}
