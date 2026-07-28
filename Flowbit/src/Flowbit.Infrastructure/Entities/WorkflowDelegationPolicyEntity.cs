namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowDelegationPolicyEntity
{
    public string WorkflowKey { get; set; } = string.Empty;

    public bool RequiresAcceptance { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
