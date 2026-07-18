using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class SequenceFlowOccurrenceEntity
{
    public long Id { get; set; }

    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public int SequenceFlowId { get; set; }

    public int SourceNodeId { get; set; }

    public int TargetNodeId { get; set; }

    public long? TokenId { get; set; }

    public long? UserTaskId { get; set; }

    public long? MultiInstanceExecutionId { get; set; }

    public int? ItemIndex { get; set; }

    public string Kind { get; set; } = string.Empty;

    public bool IsAction { get; set; }

    public bool IsTraversal { get; set; }

    public string? User { get; set; }

    public List<string> UserRoles { get; set; } = [];

    public JsonDocument? ValuesJson { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
