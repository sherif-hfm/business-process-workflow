namespace WorkflowEngine.Infrastructure.Entities;

public sealed class WorkflowBusinessKeyClaimEntity
{
    public string WorkflowKey { get; set; } = string.Empty;

    public string BusinessKey { get; set; } = string.Empty;

    public bool IsPermanent { get; set; }

    public long? ActiveInstanceId { get; set; }

    public long? LastInstanceId { get; set; }
}
