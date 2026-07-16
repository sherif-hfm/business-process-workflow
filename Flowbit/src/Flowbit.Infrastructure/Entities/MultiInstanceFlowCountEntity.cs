namespace Flowbit.Infrastructure.Entities;

public sealed class MultiInstanceFlowCountEntity
{
    public long Id { get; set; }
    public long ExecutionId { get; set; }
    public MultiInstanceExecutionEntity? Execution { get; set; }
    public int FlowId { get; set; }
    public int CompletedCount { get; set; }
}
