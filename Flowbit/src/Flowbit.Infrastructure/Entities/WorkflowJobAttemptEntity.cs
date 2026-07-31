namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowJobAttemptEntity
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public WorkflowJobEntity? Job { get; set; }
    public int AttemptNumber { get; set; }
    public string Status { get; set; } = "running";
    public string? WorkerId { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureDescription { get; set; }
}
