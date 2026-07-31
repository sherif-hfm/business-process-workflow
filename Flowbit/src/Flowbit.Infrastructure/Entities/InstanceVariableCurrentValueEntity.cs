using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class InstanceVariableCurrentValueEntity
{
    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public string VariableName { get; set; } = string.Empty;

    public long SourceVariableId { get; set; }

    public JsonDocument ValueJson { get; set; } = JsonDocument.Parse("null");

    public DateTimeOffset SetAt { get; set; }
}
