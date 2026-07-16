using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class InstanceVariableEntity
{
    public long Id { get; set; }

    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public string VariableName { get; set; } = string.Empty;

    public int? SourceActionId { get; set; }

    public JsonDocument ValueJson { get; set; } = JsonDocument.Parse("null");

    public string? SetBy { get; set; }

    public DateTimeOffset SetAt { get; set; } = DateTimeOffset.UtcNow;
}
