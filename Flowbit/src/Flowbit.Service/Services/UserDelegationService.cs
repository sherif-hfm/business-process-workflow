using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed class UserDelegationService(
    IUserDelegationRepository repository,
    IUnitOfWork unitOfWork,
    IEngineSettingsService engineSettingsService,
    TimeProvider timeProvider) : IUserDelegationService
{
    public Task<IReadOnlyList<UserDelegationDto>> CreateAsync(
        CreateUserDelegationRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var delegator = RequireActor(actor);
        return CreateCoreAsync(
            delegator,
            request.Delegate,
            request.WorkflowKeys,
            request.ValidFrom,
            request.ValidUntil,
            request.Reason,
            delegator,
            cancellationToken);
    }

    public async Task<PagedResult<UserDelegationDto>> ListAsync(
        string? direction,
        string? workflowKey,
        string? acceptanceState,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var user = RequireActor(actor);
        var normalizedDirection = string.IsNullOrWhiteSpace(direction)
            ? "outgoing"
            : direction.Trim();
        if (normalizedDirection is not ("outgoing" or "incoming"))
        {
            throw new WorkflowDomainException(
                "Delegation direction must be either 'outgoing' or 'incoming'.");
        }

        var search = new UserDelegationSearch(
            normalizedDirection == "outgoing" ? user : null,
            normalizedDirection == "incoming" ? user : null,
            NormalizeOptionalWorkflowKey(workflowKey),
            NormalizeAcceptanceState(acceptanceState),
            NormalizePage(page),
            NormalizePageSize(pageSize));
        var result = await repository.ListAsync(search, cancellationToken);
        return MapPage(result);
    }

    public Task<UserDelegationDto?> AcceptAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        CancellationToken cancellationToken) =>
        DecideAsync(
            id,
            request,
            actor,
            UserDelegationAcceptanceStates.Accepted,
            cancellationToken);

    public Task<UserDelegationDto?> RejectAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        CancellationToken cancellationToken) =>
        DecideAsync(
            id,
            request,
            actor,
            UserDelegationAcceptanceStates.Rejected,
            cancellationToken);

    public Task<UserDelegationDto?> RevokeAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        CancellationToken cancellationToken) =>
        RevokeCoreAsync(id, request, actor, requireAdmin: false, cancellationToken);

    public async Task<IReadOnlyList<UserDelegationDto>> CreateManagedAsync(
        CreateManagedUserDelegationRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var administrator = await RequireAdministratorAsync(actor, cancellationToken);
        return await CreateCoreAsync(
            request.Delegator,
            request.Delegate,
            request.WorkflowKeys,
            request.ValidFrom,
            request.ValidUntil,
            request.Reason,
            administrator,
            cancellationToken);
    }

    public async Task<PagedResult<UserDelegationDto>> ListManagedAsync(
        string? delegator,
        string? delegateUser,
        string? workflowKey,
        string? acceptanceState,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await RequireAdministratorAsync(actor, cancellationToken);
        var search = new UserDelegationSearch(
            NormalizeOptionalActor(delegator, nameof(delegator)),
            NormalizeOptionalActor(delegateUser, "delegate"),
            NormalizeOptionalWorkflowKey(workflowKey),
            NormalizeAcceptanceState(acceptanceState),
            NormalizePage(page),
            NormalizePageSize(pageSize));
        var result = await repository.ListAsync(search, cancellationToken);
        return MapPage(result);
    }

    public Task<UserDelegationDto?> RevokeManagedAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        CancellationToken cancellationToken) =>
        RevokeCoreAsync(id, request, actor, requireAdmin: true, cancellationToken);

    public async Task<WorkflowDelegationPolicyDto?> GetPolicyAsync(
        string workflowKey,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await RequireAdministratorAsync(actor, cancellationToken);
        var canonicalKey = await ResolveWorkflowKeyAsync(workflowKey, cancellationToken);
        if (canonicalKey is null)
        {
            return null;
        }

        var policy = await repository.GetPolicyAsync(
            canonicalKey,
            forUpdate: false,
            cancellationToken);
        return policy is null
            ? new WorkflowDelegationPolicyDto(canonicalKey, false, null, null, null, null)
            : MapPolicy(policy);
    }

    public async Task<WorkflowDelegationPolicyDto?> SetPolicyAsync(
        string workflowKey,
        UpdateWorkflowDelegationPolicyRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var administrator = await RequireAdministratorAsync(actor, cancellationToken);
        var canonicalKey = await ResolveWorkflowKeyAsync(workflowKey, cancellationToken);
        if (canonicalKey is null)
        {
            return null;
        }

        var now = GetUtcNow();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await repository.LockPolicyKeyAsync(canonicalKey, cancellationToken);
        var current = await repository.GetPolicyAsync(
            canonicalKey,
            forUpdate: true,
            cancellationToken);

        if (request.ExpectedUpdatedAt is { } expected
            && (current is null
                || current.UpdatedAt != NormalizeDatabaseTimestamp(expected)))
        {
            throw new WorkflowConflictException(
                "The delegation policy changed after it was loaded.");
        }

        var updated = await repository.UpsertPolicyAsync(
            canonicalKey,
            request.RequiresAcceptance,
            administrator,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapPolicy(updated);
    }

    private async Task<IReadOnlyList<UserDelegationDto>> CreateCoreAsync(
        string delegatorValue,
        string delegateValue,
        IReadOnlyList<string>? workflowKeyValues,
        DateTimeOffset validFromValue,
        DateTimeOffset validUntilValue,
        string? reasonValue,
        string createdBy,
        CancellationToken cancellationToken)
    {
        var delegator = NormalizeActor(delegatorValue, "delegator");
        var delegateUser = NormalizeActor(delegateValue, "delegate");
        if (string.Equals(delegator, delegateUser, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowDomainException("A user cannot delegate work to themselves.");
        }

        var validFrom = NormalizeDatabaseTimestamp(validFromValue);
        var validUntil = NormalizeDatabaseTimestamp(validUntilValue);
        var now = GetUtcNow();
        if (validUntil <= validFrom)
        {
            throw new WorkflowDomainException(
                "Delegation ValidUntil must be later than ValidFrom.");
        }
        if (validUntil <= now)
        {
            throw new WorkflowDomainException(
                "Delegation ValidUntil must be in the future.");
        }

        var reason = NormalizeReason(reasonValue);
        var requestedKeys = NormalizeWorkflowKeys(workflowKeyValues);
        var resolvedKeys = await repository.ResolveWorkflowKeysAsync(
            requestedKeys,
            cancellationToken);
        var missing = requestedKeys
            .Where(key => !resolvedKeys.ContainsKey(key))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new WorkflowDomainException(
                $"Unknown workflow key(s): {string.Join(", ", missing)}.");
        }

        var canonicalKeys = requestedKeys
            .Select(key => resolvedKeys[key])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        foreach (var workflowKey in canonicalKeys)
        {
            await repository.LockPolicyKeyAsync(workflowKey, cancellationToken);
        }
        foreach (var workflowKey in canonicalKeys)
        {
            await repository.LockGrantKeyAsync(
                delegator,
                delegateUser,
                workflowKey,
                cancellationToken);
        }

        var policies = await repository.GetPoliciesAsync(canonicalKeys, cancellationToken);
        var grants = new List<NewUserDelegationRecord>(canonicalKeys.Length);
        foreach (var workflowKey in canonicalKeys)
        {
            if (await repository.HasOverlappingGrantAsync(
                    delegator,
                    delegateUser,
                    workflowKey,
                    validFrom,
                    validUntil,
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    $"An overlapping delegation already exists for workflow '{workflowKey}'.");
            }

            var requiresAcceptance = policies.TryGetValue(workflowKey, out var policy)
                && policy.RequiresAcceptance;
            grants.Add(new NewUserDelegationRecord(
                delegator,
                delegateUser,
                workflowKey,
                validFrom,
                validUntil,
                requiresAcceptance,
                requiresAcceptance
                    ? UserDelegationAcceptanceStates.Pending
                    : UserDelegationAcceptanceStates.NotRequired,
                createdBy,
                reason,
                now));
        }

        var created = await repository.AddBatchAsync(grants, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created.Select(record => Map(record, now)).ToArray();
    }

    private async Task<UserDelegationDto?> DecideAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        string decision,
        CancellationToken cancellationToken)
    {
        var user = RequireActor(actor);
        var now = GetUtcNow();
        var reason = NormalizeReason(request.Reason);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var current = await repository.GetAsync(id, forUpdate: true, cancellationToken);
        if (current is null)
        {
            return null;
        }
        if (!string.Equals(current.Delegate, user, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowForbiddenException(
                "Only the designated delegate can accept or reject this delegation.");
        }

        EnsureExpectedVersion(current, request.ExpectedUpdatedAt);
        if (current.RevokedAt is not null)
        {
            throw new WorkflowConflictException("The delegation has already been revoked.");
        }
        if (!current.RequiresAcceptance
            || current.AcceptanceState != UserDelegationAcceptanceStates.Pending)
        {
            throw new WorkflowConflictException(
                "The delegation no longer has a pending acceptance decision.");
        }
        if (current.ValidUntil <= now)
        {
            throw new WorkflowConflictException("The delegation has already expired.");
        }

        var updated = await repository.SetDecisionAsync(
            id,
            decision,
            user,
            now,
            reason,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(updated, now);
    }

    private async Task<UserDelegationDto?> RevokeCoreAsync(
        long id,
        UserDelegationLifecycleRequest request,
        ActorContext actor,
        bool requireAdmin,
        CancellationToken cancellationToken)
    {
        var user = requireAdmin
            ? await RequireAdministratorAsync(actor, cancellationToken)
            : RequireActor(actor);
        var now = GetUtcNow();
        var reason = NormalizeReason(request.Reason);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var current = await repository.GetAsync(id, forUpdate: true, cancellationToken);
        if (current is null)
        {
            return null;
        }
        if (!requireAdmin
            && !string.Equals(current.Delegator, user, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(current.Delegate, user, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowForbiddenException(
                "Only a delegation participant can withdraw or revoke this delegation.");
        }

        EnsureExpectedVersion(current, request.ExpectedUpdatedAt);
        if (current.RevokedAt is not null)
        {
            throw new WorkflowConflictException("The delegation has already been revoked.");
        }
        if (current.AcceptanceState == UserDelegationAcceptanceStates.Rejected)
        {
            throw new WorkflowConflictException("A rejected delegation cannot be revoked.");
        }

        var updated = await repository.RevokeAsync(
            id,
            user,
            now,
            reason,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(updated, now);
    }

    private async Task<string> RequireAdministratorAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var user = RequireActor(actor);
        var setting = await engineSettingsService.GetByKeyAsync(
            UserDelegationConstraints.AdminRolesSettingKey,
            cancellationToken);
        var configuredRoles = string.IsNullOrWhiteSpace(setting?.Value)
            ? [UserDelegationConstraints.DefaultAdminRole]
            : setting.Value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (configuredRoles.Length == 0)
        {
            configuredRoles = [UserDelegationConstraints.DefaultAdminRole];
        }

        var callerRoles = actor.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!configuredRoles.Any(callerRoles.Contains))
        {
            throw new WorkflowForbiddenException(
                "The caller is not authorized to administer user delegation.");
        }

        return user;
    }

    private async Task<string?> ResolveWorkflowKeyAsync(
        string workflowKey,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeWorkflowKey(workflowKey);
        var resolved = await repository.ResolveWorkflowKeysAsync(
            [normalized],
            cancellationToken);
        return resolved.GetValueOrDefault(normalized);
    }

    private static void EnsureExpectedVersion(
        UserDelegationRecord current,
        DateTimeOffset expectedUpdatedAt)
    {
        if (current.UpdatedAt != NormalizeDatabaseTimestamp(expectedUpdatedAt))
        {
            throw new WorkflowConflictException(
                "The delegation changed after it was loaded.");
        }
    }

    private PagedResult<UserDelegationDto> MapPage(
        PagedResult<UserDelegationRecord> result)
    {
        var now = GetUtcNow();
        return new PagedResult<UserDelegationDto>(
            result.Items.Select(item => Map(item, now)).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    private static UserDelegationDto Map(
        UserDelegationRecord record,
        DateTimeOffset now) =>
        new(
            record.Id,
            record.Delegator,
            record.Delegate,
            record.WorkflowKey,
            record.ValidFrom,
            record.ValidUntil,
            record.RequiresAcceptance,
            record.AcceptanceState,
            record.CreatedBy,
            record.CreationReason,
            record.CreatedAt,
            record.DecisionBy,
            record.DecisionAt,
            record.DecisionReason,
            record.RevokedBy,
            record.RevokedAt,
            record.RevocationReason,
            record.UpdatedAt,
            record.RevokedAt is null
                && record.ValidFrom <= now
                && now < record.ValidUntil
                && UserDelegationAcceptanceStates.IsEffective(record.AcceptanceState));

    private static WorkflowDelegationPolicyDto MapPolicy(
        WorkflowDelegationPolicyRecord record) =>
        new(
            record.WorkflowKey,
            record.RequiresAcceptance,
            record.CreatedBy,
            record.CreatedAt,
            record.UpdatedBy,
            record.UpdatedAt);

    private static string RequireActor(ActorContext actor)
    {
        if (string.IsNullOrWhiteSpace(actor.User))
        {
            throw new WorkflowUnauthorizedException(
                "A valid authenticated user identity is required.");
        }
        return NormalizeActor(actor.User, "actor");
    }

    private static string NormalizeActor(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WorkflowDomainException($"{fieldName} is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > UserDelegationConstraints.MaxActorNameLength)
        {
            throw new WorkflowDomainException(
                $"{fieldName} cannot exceed {UserDelegationConstraints.MaxActorNameLength} characters.");
        }
        return normalized;
    }

    private static string? NormalizeOptionalActor(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeActor(value, fieldName);

    private static string NormalizeWorkflowKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WorkflowDomainException("workflowKey is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > UserDelegationConstraints.MaxWorkflowKeyLength)
        {
            throw new WorkflowDomainException(
                $"workflowKey cannot exceed {UserDelegationConstraints.MaxWorkflowKeyLength} characters.");
        }
        return normalized;
    }

    private static string? NormalizeOptionalWorkflowKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeWorkflowKey(value);

    private static IReadOnlyList<string> NormalizeWorkflowKeys(
        IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            throw new WorkflowDomainException("At least one workflow key is required.");
        }
        if (values.Count > UserDelegationConstraints.MaxWorkflowKeysPerBatch)
        {
            throw new WorkflowDomainException(
                $"No more than {UserDelegationConstraints.MaxWorkflowKeysPerBatch} workflow keys may be delegated at once.");
        }

        var normalized = values
            .Select(NormalizeWorkflowKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length != values.Count)
        {
            throw new WorkflowDomainException(
                "Workflow keys in a delegation request must be unique.");
        }
        return normalized;
    }

    private static string? NormalizeAcceptanceState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        foreach (var state in new[]
                 {
                     UserDelegationAcceptanceStates.NotRequired,
                     UserDelegationAcceptanceStates.Pending,
                     UserDelegationAcceptanceStates.Accepted,
                     UserDelegationAcceptanceStates.Rejected
                 })
        {
            if (string.Equals(normalized, state, StringComparison.OrdinalIgnoreCase))
            {
                return state;
            }
        }
        throw new WorkflowDomainException(
            "Acceptance state must be notRequired, pending, accepted, or rejected.");
    }

    private static string? NormalizeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var normalized = value.Trim();
        if (normalized.Length > UserDelegationConstraints.MaxReasonLength)
        {
            throw new WorkflowDomainException(
                $"Reason cannot exceed {UserDelegationConstraints.MaxReasonLength} characters.");
        }
        return normalized;
    }

    private static int NormalizePage(int page) => Math.Max(1, page);

    private static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize, 1, 200);

    private DateTimeOffset GetUtcNow() =>
        NormalizeDatabaseTimestamp(timeProvider.GetUtcNow());

    // PostgreSQL timestamptz stores microseconds while DateTimeOffset uses
    // 100-nanosecond ticks. Returning the exact persisted precision keeps
    // ExpectedUpdatedAt round trips deterministic.
    private static DateTimeOffset NormalizeDatabaseTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var ticks = utc.Ticks - (utc.Ticks % 10);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
