namespace Flowbit.Infrastructure.Entities;

public sealed class UserDelegationEntity
{
    public long Id { get; set; }

    public string Delegator { get; set; } = string.Empty;

    public string Delegate { get; set; } = string.Empty;

    public string WorkflowKey { get; set; } = string.Empty;

    public DateTimeOffset ValidFrom { get; set; }

    public DateTimeOffset ValidUntil { get; set; }

    public bool RequiresAcceptance { get; set; }

    public string AcceptanceState { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public string? CreationReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? DecisionBy { get; set; }

    public DateTimeOffset? DecisionAt { get; set; }

    public string? DecisionReason { get; set; }

    public string? RevokedBy { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevocationReason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
