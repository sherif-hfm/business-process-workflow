using Flowbit.Shared.Models;

namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowDefinitionEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string WorkflowKey { get; set; } = string.Empty;

    public int Version { get; set; }

    public WorkflowModel Definition { get; set; } = new();

    public bool IsPublished { get; set; }

    public bool IsDefault { get; set; }

    public Guid? DefaultActivationId { get; set; }

    public DateTimeOffset? DefaultActivatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<WorkflowInstanceEntity> Instances { get; set; } = [];

    public List<NodeExecutionEntity> NodeExecutions { get; set; } = [];

    public List<InstanceHistoryEntity> InstanceHistory { get; set; } = [];

    public List<SequenceFlowOccurrenceEntity> SequenceFlowOccurrences { get; set; } = [];

    public List<WorkflowInstanceVersionChangeEntity> SourceVersionChanges { get; set; } = [];

    public List<WorkflowInstanceVersionChangeEntity> TargetVersionChanges { get; set; } = [];

    public List<AdministrativeActionBatchItemEntity> AdministrativeActionBatchItems { get; set; } = [];
}
