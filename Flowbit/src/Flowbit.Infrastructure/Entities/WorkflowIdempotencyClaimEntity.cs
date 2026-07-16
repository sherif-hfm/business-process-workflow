namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowIdempotencyClaimEntity
{
    public string WorkflowKey { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public long? InstanceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
