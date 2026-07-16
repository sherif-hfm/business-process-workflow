using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class InstanceHistoryEntity
{
    public long Id { get; set; }

    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public int? ActionId { get; set; }

    public int FromStepId { get; set; }

    public int ToStepId { get; set; }

    public string? PerformedBy { get; set; }

    public JsonDocument? Payload { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset PerformedAt { get; set; } = DateTimeOffset.UtcNow;
    public long? TokenId { get; set; }
    public long? UserTaskId { get; set; }
    public long? MultiInstanceExecutionId { get; set; }
    public int? ItemIndex { get; set; }
}
