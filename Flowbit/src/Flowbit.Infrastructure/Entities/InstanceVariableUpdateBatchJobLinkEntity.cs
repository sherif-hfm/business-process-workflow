namespace Flowbit.Infrastructure.Entities;

public sealed class InstanceVariableUpdateBatchJobLinkEntity
{
    public long Id { get; set; }

    public long BatchId { get; set; }

    public InstanceVariableUpdateBatchEntity? Batch { get; set; }

    public long WorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }

    public string Phase { get; set; } = string.Empty;

    public long OriginalJobId { get; set; }

    public long? JobId { get; set; }

    public WorkflowJobEntity? Job { get; set; }
}
