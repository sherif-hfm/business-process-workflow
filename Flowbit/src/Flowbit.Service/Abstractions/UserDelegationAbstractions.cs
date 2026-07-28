using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IUserDelegationRepository
{
    /// <summary>
    /// Resolves supplied workflow keys to their existing canonical family keys.
    /// Dictionary lookup is case-insensitive; missing families are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveWorkflowKeysAsync(
        IReadOnlyCollection<string> workflowKeys,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, WorkflowDelegationPolicyRecord>> GetPoliciesAsync(
        IReadOnlyCollection<string> workflowKeys,
        CancellationToken cancellationToken);

    /// <summary>
    /// Serializes policy reads used by grant creation with policy updates.
    /// Must run in a transaction.
    /// </summary>
    Task LockPolicyKeyAsync(
        string workflowKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Serializes grant creation for one case-insensitive
    /// delegator/delegate/workflow-family tuple. Must run in a transaction.
    /// </summary>
    Task LockGrantKeyAsync(
        string delegator,
        string delegateUser,
        string workflowKey,
        CancellationToken cancellationToken);

    Task<bool> HasOverlappingGrantAsync(
        string delegator,
        string delegateUser,
        string workflowKey,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserDelegationRecord>> AddBatchAsync(
        IReadOnlyCollection<NewUserDelegationRecord> grants,
        CancellationToken cancellationToken);

    Task<PagedResult<UserDelegationRecord>> ListAsync(
        UserDelegationSearch search,
        CancellationToken cancellationToken);

    Task<UserDelegationRecord?> GetAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds a currently effective direct grant for a delegate acting for an owner.
    /// With <paramref name="forUpdate"/> true, the caller must already own a transaction.
    /// </summary>
    Task<UserDelegationRecord?> ResolveActiveAsync(
        string delegateUser,
        string ownerUser,
        string workflowKey,
        DateTimeOffset asOf,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<UserDelegationRecord> SetDecisionAsync(
        long id,
        string acceptanceState,
        string actor,
        DateTimeOffset decidedAt,
        string? reason,
        CancellationToken cancellationToken);

    Task<UserDelegationRecord> RevokeAsync(
        long id,
        string actor,
        DateTimeOffset revokedAt,
        string? reason,
        CancellationToken cancellationToken);

    Task<WorkflowDelegationPolicyRecord?> GetPolicyAsync(
        string workflowKey,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<WorkflowDelegationPolicyRecord> UpsertPolicyAsync(
        string workflowKey,
        bool requiresAcceptance,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public interface IUserDelegationService
{
    Task<IReadOnlyList<UserDelegationDto>> CreateAsync(
        CreateUserDelegationRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<UserDelegationDto>> ListAsync(
        string? direction,
        string? workflowKey,
        string? acceptanceState,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<UserDelegationDto?> AcceptAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<UserDelegationDto?> RejectAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<UserDelegationDto?> RevokeAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserDelegationDto>> CreateManagedAsync(
        CreateManagedUserDelegationRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<PagedResult<UserDelegationDto>> ListManagedAsync(
        string? delegator,
        string? delegateUser,
        string? workflowKey,
        string? acceptanceState,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<UserDelegationDto?> RevokeManagedAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<WorkflowDelegationPolicyDto?> GetPolicyAsync(
        string workflowKey,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<WorkflowDelegationPolicyDto?> SetPolicyAsync(
        string workflowKey,
        UpdateWorkflowDelegationPolicyRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);
}
