namespace Flowbit.Shared.Dtos;

/// <summary>
/// Creates one standing delegation grant for each requested workflow family.
/// The authenticated caller is always used as the delegator.
/// </summary>
public sealed record CreateUserDelegationRequest(
    string Delegate,
    IReadOnlyList<string> WorkflowKeys,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    string? Reason = null);

/// <summary>
/// Administrative form of delegation creation. The authenticated administrator
/// remains the creation actor while <see cref="Delegator"/> is the represented user.
/// </summary>
public sealed record CreateManagedUserDelegationRequest(
    string Delegator,
    string Delegate,
    IReadOnlyList<string> WorkflowKeys,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    string? Reason = null);

/// <summary>
/// Optimistic lifecycle command used to accept, reject, withdraw, or revoke a grant.
/// </summary>
public sealed record UserDelegationLifecycleRequest(
    DateTimeOffset ExpectedUpdatedAt,
    string? Reason = null);

/// <summary>
/// A retained standing delegation grant for one stable workflow family.
/// </summary>
public sealed record UserDelegationDto(
    long Id,
    string Delegator,
    string Delegate,
    string WorkflowKey,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    bool RequiresAcceptance,
    string AcceptanceState,
    string CreatedBy,
    string? CreationReason,
    DateTimeOffset CreatedAt,
    string? DecisionBy,
    DateTimeOffset? DecisionAt,
    string? DecisionReason,
    string? RevokedBy,
    DateTimeOffset? RevokedAt,
    string? RevocationReason,
    DateTimeOffset UpdatedAt,
    bool IsActive);

/// <summary>
/// Workflow-family policy controlling whether new grants require delegate acceptance.
/// A missing persisted policy is represented by the default values and null audit fields.
/// </summary>
public sealed record WorkflowDelegationPolicyDto(
    string WorkflowKey,
    bool RequiresAcceptance,
    string? CreatedBy,
    DateTimeOffset? CreatedAt,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// Creates or updates a workflow-family delegation policy.
/// </summary>
public sealed record UpdateWorkflowDelegationPolicyRequest(
    bool RequiresAcceptance,
    DateTimeOffset? ExpectedUpdatedAt = null);
