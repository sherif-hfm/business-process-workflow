using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Infrastructure.Entities;

public sealed class WorkflowDefinitionEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string WorkflowKey { get; set; } = string.Empty;

    public int Version { get; set; }

    public WorkflowModel Definition { get; set; } = new();

    public bool IsPublished { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<WorkflowInstanceEntity> Instances { get; set; } = [];
}
