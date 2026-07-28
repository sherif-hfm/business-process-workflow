using Microsoft.EntityFrameworkCore;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Infrastructure.Repositories;

public sealed class UserDelegationRepository(AppDbContext dbContext)
    : IUserDelegationRepository
{
    public async Task<IReadOnlyDictionary<string, string>> ResolveWorkflowKeysAsync(
        IReadOnlyCollection<string> workflowKeys,
        CancellationToken cancellationToken)
    {
        if (workflowKeys.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var requested = workflowKeys
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalized = requested
            .Select(key => key.ToLowerInvariant())
            .ToArray();
        var candidates = await dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Where(definition => normalized.Contains(definition.WorkflowKey.ToLower()))
            .Select(definition => definition.WorkflowKey)
            .Distinct()
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in requested)
        {
            var matches = candidates
                .Where(candidate => string.Equals(
                    candidate,
                    input,
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (matches.Length == 0)
            {
                continue;
            }

            var exact = matches.FirstOrDefault(candidate =>
                string.Equals(candidate, input, StringComparison.Ordinal));
            if (exact is not null)
            {
                result[input] = exact;
            }
            else if (matches.Length == 1)
            {
                result[input] = matches[0];
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, WorkflowDelegationPolicyRecord>> GetPoliciesAsync(
        IReadOnlyCollection<string> workflowKeys,
        CancellationToken cancellationToken)
    {
        if (workflowKeys.Count == 0)
        {
            return new Dictionary<string, WorkflowDelegationPolicyRecord>(
                StringComparer.OrdinalIgnoreCase);
        }

        var entities = await dbContext.WorkflowDelegationPolicies
            .AsNoTracking()
            .Where(policy => workflowKeys.Contains(policy.WorkflowKey))
            .ToListAsync(cancellationToken);
        return entities.ToDictionary(
            entity => entity.WorkflowKey,
            ToPolicyRecord,
            StringComparer.OrdinalIgnoreCase);
    }

    public Task LockPolicyKeyAsync(
        string workflowKey,
        CancellationToken cancellationToken)
    {
        var lockKey = workflowKey.ToUpperInvariant();
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('delegation-policy'), hashtext({lockKey}))",
            cancellationToken);
    }

    public Task LockGrantKeyAsync(
        string delegator,
        string delegateUser,
        string workflowKey,
        CancellationToken cancellationToken)
    {
        var lockKey = string.Join(
            '\u001f',
            delegator.ToUpperInvariant(),
            delegateUser.ToUpperInvariant(),
            workflowKey.ToUpperInvariant());
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('user-delegation'), hashtext({lockKey}))",
            cancellationToken);
    }

    public Task<bool> HasOverlappingGrantAsync(
        string delegator,
        string delegateUser,
        string workflowKey,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken) =>
        dbContext.UserDelegations
            .AsNoTracking()
            .AnyAsync(
                grant =>
                    grant.Delegator == delegator
                    && grant.Delegate == delegateUser
                    && grant.WorkflowKey == workflowKey
                    && grant.RevokedAt == null
                    && grant.AcceptanceState != UserDelegationAcceptanceStates.Rejected
                    && grant.ValidFrom < validUntil
                    && validFrom < grant.ValidUntil,
                cancellationToken);

    public async Task<IReadOnlyList<UserDelegationRecord>> AddBatchAsync(
        IReadOnlyCollection<NewUserDelegationRecord> grants,
        CancellationToken cancellationToken)
    {
        var entities = grants.Select(grant => new UserDelegationEntity
        {
            Delegator = grant.Delegator,
            Delegate = grant.Delegate,
            WorkflowKey = grant.WorkflowKey,
            ValidFrom = grant.ValidFrom,
            ValidUntil = grant.ValidUntil,
            RequiresAcceptance = grant.RequiresAcceptance,
            AcceptanceState = grant.AcceptanceState,
            CreatedBy = grant.CreatedBy,
            CreationReason = grant.CreationReason,
            CreatedAt = grant.CreatedAt,
            UpdatedAt = grant.CreatedAt
        }).ToArray();
        dbContext.UserDelegations.AddRange(entities);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entities.Select(ToRecord).ToArray();
    }

    public async Task<PagedResult<UserDelegationRecord>> ListAsync(
        UserDelegationSearch search,
        CancellationToken cancellationToken)
    {
        IQueryable<UserDelegationEntity> query = dbContext.UserDelegations.AsNoTracking();
        if (search.Delegator is not null)
        {
            query = query.Where(grant => grant.Delegator == search.Delegator);
        }
        if (search.Delegate is not null)
        {
            query = query.Where(grant => grant.Delegate == search.Delegate);
        }
        if (search.WorkflowKey is not null)
        {
            query = query.Where(grant => grant.WorkflowKey == search.WorkflowKey);
        }
        if (search.AcceptanceState is not null)
        {
            query = query.Where(grant =>
                grant.AcceptanceState == search.AcceptanceState);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);
        var entities = await query
            .OrderByDescending(grant => grant.UpdatedAt)
            .ThenByDescending(grant => grant.Id)
            .Skip((search.Page - 1) * search.PageSize)
            .Take(search.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<UserDelegationRecord>(
            entities.Select(ToRecord).ToArray(),
            search.Page,
            search.PageSize,
            totalCount);
    }

    public async Task<UserDelegationRecord?> GetAsync(
        long id,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var entity = forUpdate
            ? await dbContext.UserDelegations
                .FromSqlInterpolated(
                    $"SELECT * FROM flowbit.user_delegations WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.UserDelegations
                .AsNoTracking()
                .SingleOrDefaultAsync(grant => grant.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<UserDelegationRecord?> ResolveActiveAsync(
        string delegateUser,
        string ownerUser,
        string workflowKey,
        DateTimeOffset asOf,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        UserDelegationEntity? entity;
        if (forUpdate)
        {
            entity = await dbContext.UserDelegations
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM flowbit.user_delegations
                    WHERE "Delegate" = {delegateUser}
                      AND "Delegator" = {ownerUser}
                      AND "WorkflowKey" = {workflowKey}
                      AND "RevokedAt" IS NULL
                      AND "ValidFrom" <= {asOf}
                      AND {asOf} < "ValidUntil"
                      AND "AcceptanceState" IN ('notRequired', 'accepted')
                    ORDER BY "ValidFrom" DESC, "Id" DESC
                    LIMIT 1
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            entity = await dbContext.UserDelegations
                .AsNoTracking()
                .Where(grant =>
                    grant.Delegate == delegateUser
                    && grant.Delegator == ownerUser
                    && grant.WorkflowKey == workflowKey
                    && grant.RevokedAt == null
                    && grant.ValidFrom <= asOf
                    && asOf < grant.ValidUntil
                    && (grant.AcceptanceState == UserDelegationAcceptanceStates.NotRequired
                        || grant.AcceptanceState == UserDelegationAcceptanceStates.Accepted))
                .OrderByDescending(grant => grant.ValidFrom)
                .ThenByDescending(grant => grant.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<UserDelegationRecord> SetDecisionAsync(
        long id,
        string acceptanceState,
        string actor,
        DateTimeOffset decidedAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        var entity = await GetTrackedAsync(id, cancellationToken);
        entity.AcceptanceState = acceptanceState;
        entity.DecisionBy = actor;
        entity.DecisionAt = decidedAt;
        entity.DecisionReason = reason;
        entity.UpdatedAt = decidedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<UserDelegationRecord> RevokeAsync(
        long id,
        string actor,
        DateTimeOffset revokedAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        var entity = await GetTrackedAsync(id, cancellationToken);
        entity.RevokedBy = actor;
        entity.RevokedAt = revokedAt;
        entity.RevocationReason = reason;
        entity.UpdatedAt = revokedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<WorkflowDelegationPolicyRecord?> GetPolicyAsync(
        string workflowKey,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var entity = forUpdate
            ? await dbContext.WorkflowDelegationPolicies
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM flowbit.workflow_delegation_policies
                    WHERE "WorkflowKey" = {workflowKey}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.WorkflowDelegationPolicies
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    policy => policy.WorkflowKey == workflowKey,
                    cancellationToken);
        return entity is null ? null : ToPolicyRecord(entity);
    }

    public async Task<WorkflowDelegationPolicyRecord> UpsertPolicyAsync(
        string workflowKey,
        bool requiresAcceptance,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.WorkflowDelegationPolicies.Local
            .SingleOrDefault(policy => policy.WorkflowKey == workflowKey);
        entity ??= await dbContext.WorkflowDelegationPolicies
            .SingleOrDefaultAsync(
                policy => policy.WorkflowKey == workflowKey,
                cancellationToken);
        if (entity is null)
        {
            entity = new WorkflowDelegationPolicyEntity
            {
                WorkflowKey = workflowKey,
                RequiresAcceptance = requiresAcceptance,
                CreatedBy = actor,
                CreatedAt = now,
                UpdatedBy = actor,
                UpdatedAt = now
            };
            dbContext.WorkflowDelegationPolicies.Add(entity);
        }
        else
        {
            entity.RequiresAcceptance = requiresAcceptance;
            entity.UpdatedBy = actor;
            entity.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToPolicyRecord(entity);
    }

    private async Task<UserDelegationEntity> GetTrackedAsync(
        long id,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.UserDelegations.Local
            .SingleOrDefault(grant => grant.Id == id);
        entity ??= await dbContext.UserDelegations
            .SingleOrDefaultAsync(grant => grant.Id == id, cancellationToken);
        return entity ?? throw new InvalidOperationException(
            $"Delegation {id} disappeared while its row lock was held.");
    }

    private static UserDelegationRecord ToRecord(UserDelegationEntity entity) =>
        new(
            entity.Id,
            entity.Delegator,
            entity.Delegate,
            entity.WorkflowKey,
            entity.ValidFrom,
            entity.ValidUntil,
            entity.RequiresAcceptance,
            entity.AcceptanceState,
            entity.CreatedBy,
            entity.CreationReason,
            entity.CreatedAt,
            entity.DecisionBy,
            entity.DecisionAt,
            entity.DecisionReason,
            entity.RevokedBy,
            entity.RevokedAt,
            entity.RevocationReason,
            entity.UpdatedAt);

    private static WorkflowDelegationPolicyRecord ToPolicyRecord(
        WorkflowDelegationPolicyEntity entity) =>
        new(
            entity.WorkflowKey,
            entity.RequiresAcceptance,
            entity.CreatedBy,
            entity.CreatedAt,
            entity.UpdatedBy,
            entity.UpdatedAt);
}
