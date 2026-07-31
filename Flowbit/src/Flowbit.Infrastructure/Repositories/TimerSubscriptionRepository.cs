using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class TimerSubscriptionRepository(AppDbContext dbContext)
    : ITimerSubscriptionRepository
{
    public async Task<TimerSubscriptionRecord> CreateAsync(
        TimerSubscriptionCreateRecord create,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new TimerSubscriptionEntity
        {
            InstanceId = create.InstanceId,
            WorkflowDefinitionId = create.WorkflowDefinitionId,
            WorkflowKey = create.WorkflowKey,
            TokenId = create.TokenId,
            ActivationId = create.ActivationId,
            TimerNodeId = create.TimerNodeId,
            TimerNodeName = create.TimerNodeName,
            AttachedToNodeId = create.AttachedToNodeId,
            ScheduleKind = create.ScheduleKind,
            ScheduleExpression = create.ScheduleExpression,
            CancelActivity = create.CancelActivity,
            Status = TimerSubscriptionStatuses.Active,
            NextDueAt = create.NextDueAt,
            Occurrence = create.Occurrence,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.TimerSubscriptions.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
                  {
                      SqlState: PostgresErrorCodes.UniqueViolation
                  })
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            var existingQuery = dbContext.TimerSubscriptions.AsNoTracking();
            existingQuery = create.InstanceId is null
                ? existingQuery.Where(subscription =>
                    subscription.InstanceId == null
                    && subscription.WorkflowDefinitionId == create.WorkflowDefinitionId
                    && subscription.TimerNodeId == create.TimerNodeId
                    && (subscription.Status == TimerSubscriptionStatuses.Active
                        || subscription.Status == TimerSubscriptionStatuses.Paused))
                : existingQuery.Where(subscription =>
                    subscription.InstanceId == create.InstanceId
                    && subscription.TokenId == create.TokenId
                    && subscription.ActivationId == create.ActivationId
                    && subscription.TimerNodeId == create.TimerNodeId);
            var existing = await existingQuery.SingleAsync(cancellationToken);
            return Map(existing);
        }
        return Map(entity);
    }

    public async Task<TimerSubscriptionRecord?> GetForUpdateAsync(
        long subscriptionId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.TimerSubscriptions
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM flowbit.timer_subscriptions
                WHERE "Id" = {subscriptionId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<TimerSubscriptionRecord>> ListForActivationAsync(
        long tokenId,
        Guid activationId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.TimerSubscriptions
            .AsNoTracking()
            .Where(subscription =>
                subscription.TokenId == tokenId
                && subscription.ActivationId == activationId)
            .OrderBy(subscription => subscription.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task<bool> AdvanceAsync(
        long subscriptionId,
        long expectedOccurrence,
        long nextOccurrence,
        DateTimeOffset nextDueAt,
        bool complete,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.TimerSubscriptions
            .Where(subscription =>
                subscription.Id == subscriptionId
                && subscription.Status == TimerSubscriptionStatuses.Active
                && subscription.Occurrence == expectedOccurrence)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(subscription => subscription.Occurrence, nextOccurrence)
                .SetProperty(subscription => subscription.NextDueAt, nextDueAt)
                .SetProperty(
                    subscription => subscription.Status,
                    complete
                        ? TimerSubscriptionStatuses.Completed
                        : TimerSubscriptionStatuses.Active)
                .SetProperty(
                    subscription => subscription.CompletedAt,
                    complete ? now : (DateTimeOffset?)null)
                .SetProperty(subscription => subscription.UpdatedAt, now),
                cancellationToken);
        return affected == 1;
    }

    public async Task<bool> PauseAsync(
        long subscriptionId,
        long expectedOccurrence,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.TimerSubscriptions
            .Where(subscription =>
                subscription.Id == subscriptionId
                && subscription.Status == TimerSubscriptionStatuses.Active
                && subscription.Occurrence == expectedOccurrence)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(subscription => subscription.Status, TimerSubscriptionStatuses.Paused)
                .SetProperty(subscription => subscription.UpdatedAt, now),
                cancellationToken);
        return affected == 1;
    }

    public Task<int> CancelByInstanceAsync(
        long instanceId,
        CancellationToken cancellationToken) =>
        CancelAsync(
            dbContext.TimerSubscriptions.Where(subscription =>
                subscription.InstanceId == instanceId),
            cancellationToken);

    public Task<int> CancelByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        return CancelAsync(
            dbContext.TimerSubscriptions.Where(subscription =>
                subscription.InstanceId == instanceId
                && subscription.TokenId != null
                && tokenIds.Contains(subscription.TokenId.Value)),
            cancellationToken);
    }

    public Task<int> CancelOtherForTokenAsync(
        long instanceId,
        long tokenId,
        long exceptSubscriptionId,
        CancellationToken cancellationToken) =>
        CancelAsync(
            dbContext.TimerSubscriptions.Where(subscription =>
                subscription.InstanceId == instanceId
                && subscription.TokenId == tokenId
                && subscription.Id != exceptSubscriptionId),
            cancellationToken);

    private static async Task<int> CancelAsync(
        IQueryable<TimerSubscriptionEntity> source,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await source
            .Where(subscription =>
                subscription.Status == TimerSubscriptionStatuses.Active
                || subscription.Status == TimerSubscriptionStatuses.Paused)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(subscription => subscription.Status, TimerSubscriptionStatuses.Cancelled)
                .SetProperty(subscription => subscription.CompletedAt, now)
                .SetProperty(subscription => subscription.UpdatedAt, now),
                cancellationToken);
    }

    private static TimerSubscriptionRecord Map(TimerSubscriptionEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.WorkflowDefinitionId,
            entity.WorkflowKey,
            entity.TokenId,
            entity.ActivationId,
            entity.TimerNodeId,
            entity.TimerNodeName,
            entity.AttachedToNodeId,
            entity.ScheduleKind,
            entity.ScheduleExpression,
            entity.CancelActivity,
            entity.Status,
            entity.NextDueAt,
            entity.Occurrence,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CompletedAt);
}
