namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowBusinessKeyScopeEntity
{
    public string WorkflowKey { get; set; } = string.Empty;

    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;
}
