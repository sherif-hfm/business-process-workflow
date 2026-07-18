using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class SequenceFlowSummaryEntity
{
    public long Id { get; set; }

    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public int SequenceFlowId { get; set; }

    public long ActionCount { get; set; }

    public string? LastActionUser { get; set; }

    public List<string> LastActionUserRoles { get; set; } = [];

    public DateTimeOffset? LastActionOccurredAt { get; set; }

    public string? LastActionKind { get; set; }

    public JsonDocument? LastActionValuesJson { get; set; }

    public long TraversalCount { get; set; }

    public string? LastTraversalUser { get; set; }

    public List<string> LastTraversalUserRoles { get; set; } = [];

    public DateTimeOffset? LastTraversalOccurredAt { get; set; }

    public string? LastTraversalKind { get; set; }

    public JsonDocument? LastTraversalValuesJson { get; set; }
}
