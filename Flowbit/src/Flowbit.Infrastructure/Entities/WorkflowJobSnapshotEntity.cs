using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowJobSnapshotEntity
{
    public long Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public JsonDocument? InvocationJson { get; set; }
    public JsonDocument VariablesJson { get; set; } = JsonDocument.Parse("{}");
    public JsonDocument OutputVariableVersionsJson { get; set; } = JsonDocument.Parse("{}");
    public JsonDocument? FlowInfoJson { get; set; }
    public DateTimeOffset EvaluationTime { get; set; }
    public int SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<WorkflowJobEntity> Jobs { get; set; } = [];
}
