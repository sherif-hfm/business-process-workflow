using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Worker;

public sealed class TimerStartReconciliationService(
    IServiceScopeFactory scopeFactory,
    WorkerOptions options,
    TimeProvider timeProvider,
    WorkerTelemetry telemetry,
    ILogger<TimerStartReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan MisfireGrace = TimeSpan.FromMinutes(1);
    private string? _familyCursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(options.TimerStartReconcileSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Timer-start reconciliation failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var definitionRepository =
            scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionRepository>();
        var subscriptions = scope.ServiceProvider.GetRequiredService<ITimerSubscriptionRepository>();
        var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Reconciliation is repair/control-plane work, not timer firing. Let a
        // single replica own each short reconciliation transaction so replicas
        // do not all scan and lock every workflow family once per second. The
        // transaction-scoped try-lock releases automatically on crash or normal
        // completion, so another replica takes over on its next poll.
        var isLeader = await dbContext.Database
            .SqlQueryRaw<bool>(
                """
                SELECT pg_try_advisory_xact_lock(
                    hashtext('flowbit.worker'),
                    hashtext('timer-start-reconciliation')) AS "Value"
                """)
            .SingleAsync(cancellationToken);
        if (!isLeader)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var familyKeys = await ReadFamilyBatchAsync(
            dbContext,
            _familyCursor,
            cancellationToken);
        if (familyKeys.Length == 0 && _familyCursor is not null)
        {
            // Wrap after the lexicographic tail. The cursor is deliberately
            // replica-local; a failover replica safely begins at the head and
            // repairs every family in bounded batches.
            familyKeys = await ReadFamilyBatchAsync(
                dbContext,
                cursor: null,
                cancellationToken);
        }
        if (familyKeys.Length == 0)
        {
            _familyCursor = null;
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        foreach (var workflowKey in familyKeys)
        {
            await definitionRepository.LockFamilyForStartAsync(
                workflowKey,
                cancellationToken);
        }

        // Lock current default rows in deterministic order. This makes the
        // subscription-existence check and subscription/job creation one
        // mutation batch across worker replicas. A concurrent default switch
        // may be observed on the next reconciliation; timer firing separately
        // revalidates default ownership.
        var definitions = await dbContext.WorkflowDefinitions
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM flowbit.workflow_definitions
                WHERE "IsPublished" = TRUE
                  AND "IsDefault" = TRUE
                  AND "WorkflowKey" = ANY ({familyKeys})
                ORDER BY "Id"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        var expected = definitions
            .SelectMany(definition => definition.Definition.FlowNodes
                .Where(node => BpmnFlowNodeTypes.IsTimerStart(node.Type))
                .Select(node => (
                    Definition: definition,
                    Node: node,
                    ActivationId: definition.DefaultActivationId
                        ?? throw new InvalidOperationException(
                            $"Default workflow definition #{definition.Id} has no activation generation."),
                    ActivatedAt: definition.DefaultActivatedAt
                        ?? throw new InvalidOperationException(
                            $"Default workflow definition #{definition.Id} has no activation time."))))
            .ToList();
        var expectedGenerations = expected.ToDictionary(
            item => (item.Definition.Id, item.Node.Id),
            item => item.ActivationId);

        var latestTimerStarts = await dbContext.TimerSubscriptions
            .Where(subscription =>
                subscription.InstanceId == null
                && familyKeys.Contains(subscription.WorkflowKey))
            .GroupBy(subscription => new
            {
                subscription.WorkflowDefinitionId,
                subscription.TimerNodeId
            })
            .Select(group => group
                .OrderByDescending(subscription => subscription.Id)
                .First())
            .ToListAsync(cancellationToken);
        var stale = latestTimerStarts
            .Where(subscription =>
                subscription.Status is TimerSubscriptionStatuses.Active
                    or TimerSubscriptionStatuses.Paused
                && (!expectedGenerations.TryGetValue(
                        (subscription.WorkflowDefinitionId, subscription.TimerNodeId),
                        out var expectedActivation)
                    || subscription.ActivationId != expectedActivation))
            .Select(subscription => subscription.Id)
            .ToList();
        if (stale.Count > 0)
        {
            var now = timeProvider.GetUtcNow();
            // Keep occurrence cancellation in the same job -> subscription
            // order used by timer-start finalization.
            await jobs.CancelByTimerSubscriptionIdsAsync(
                stale,
                "Timer-start definition is no longer the published default.",
                cancellationToken);
            await dbContext.TimerSubscriptions
                .Where(subscription => stale.Contains(subscription.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(subscription => subscription.Status, TimerSubscriptionStatuses.Cancelled)
                    .SetProperty(
                        subscription => subscription.CompletedAt,
                        subscription => subscription.CompletedAt ?? now)
                    .SetProperty(subscription => subscription.UpdatedAt, now),
                    cancellationToken);
        }

        foreach (var item in expected)
        {
            await EnsureTimerStartAsync(
                item.Definition.Id,
                item.Definition.WorkflowKey,
                item.Node,
                item.ActivationId,
                item.ActivatedAt,
                dbContext,
                subscriptions,
                jobs,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _familyCursor = familyKeys.Length < options.TimerStartReconcileBatchSize
            ? null
            : familyKeys[^1];
    }

    private async Task<string[]> ReadFamilyBatchAsync(
        AppDbContext dbContext,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var query = dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Where(definition => definition.IsPublished && definition.IsDefault)
            .Select(definition => definition.WorkflowKey)
            .Concat(dbContext.TimerSubscriptions
                .AsNoTracking()
                .Where(subscription =>
                    subscription.InstanceId == null
                    && (subscription.Status == TimerSubscriptionStatuses.Active
                        || subscription.Status == TimerSubscriptionStatuses.Paused))
                .Select(subscription => subscription.WorkflowKey))
            .Distinct();
        if (cursor is not null)
        {
            query = query.Where(workflowKey => string.Compare(workflowKey, cursor) > 0);
        }

        return (await query
                .OrderBy(workflowKey => workflowKey)
                .Take(options.TimerStartReconcileBatchSize)
                .ToListAsync(cancellationToken))
            .ToArray();
    }

    private async Task EnsureTimerStartAsync(
        long definitionId,
        string workflowKey,
        FlowNodeModel node,
        Guid defaultActivationId,
        DateTimeOffset defaultActivatedAt,
        AppDbContext dbContext,
        ITimerSubscriptionRepository subscriptions,
        IWorkflowJobRepository jobs,
        CancellationToken cancellationToken)
    {
        var timer = node.Timer
            ?? throw new InvalidOperationException($"Timer start #{node.Id} has no schedule.");

        var latest = await dbContext.TimerSubscriptions
            .AsNoTracking()
            .Where(subscription =>
                subscription.InstanceId == null
                && subscription.WorkflowDefinitionId == definitionId
                && subscription.TimerNodeId == node.Id)
            .OrderByDescending(subscription => subscription.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest?.ActivationId == defaultActivationId
            && latest.Status == TimerSubscriptionStatuses.Completed)
        {
            // A completed one-shot or finite cycle remains completed while this
            // immutable definition stays default. Partial uniqueness excludes
            // terminal rows, so explicitly suppress accidental recreation.
            return;
        }

        if (latest?.ActivationId == defaultActivationId
            && latest.Status is TimerSubscriptionStatuses.Active
                or TimerSubscriptionStatuses.Paused)
        {
            var hasOccurrenceJob = await dbContext.WorkflowJobs
                .AsNoTracking()
                .AnyAsync(
                    job => job.TimerSubscriptionId == latest.Id
                        && job.ActivationId == defaultActivationId
                        && job.ScheduledOccurrenceAt == latest.NextDueAt
                        && job.Status != WorkflowJobStatuses.Completed
                        && job.Status != WorkflowJobStatuses.Cancelled
                        && job.Status != WorkflowJobStatuses.Skipped,
                    cancellationToken);
            if (hasOccurrenceJob)
            {
                return;
            }

            // Repair the only legacy/crash window that predates the transaction
            // below: an active subscription without its outstanding occurrence.
            await EnqueueTimerStartAsync(
                definitionId,
                workflowKey,
                node,
                latest.Id,
                latest.ActivationId,
                latest.NextDueAt,
                jobs,
                cancellationToken);
            telemetry.RecordTimerStart();
            return;
        }

        var now = timeProvider.GetUtcNow();
        var first = ResolveInitial(timer, defaultActivatedAt, now);
        if (first is null)
        {
            return;
        }

        var subscription = await subscriptions.CreateAsync(
                new TimerSubscriptionCreateRecord
                {
                    WorkflowDefinitionId = definitionId,
                    WorkflowKey = workflowKey,
                    ActivationId = defaultActivationId,
                    TimerNodeId = node.Id,
                    TimerNodeName = node.Name,
                    ScheduleKind = ScheduleKind(timer),
                    ScheduleExpression = ScheduleExpression(timer),
                    CancelActivity = true,
                    NextDueAt = first.Value.DueAt,
                    Occurrence = first.Value.Occurrence
                },
                cancellationToken);
        if (subscription.Status != TimerSubscriptionStatuses.Active)
        {
            return;
        }

        await EnqueueTimerStartAsync(
            definitionId,
            workflowKey,
            node,
            subscription.Id,
            subscription.ActivationId,
            subscription.NextDueAt,
            jobs,
            cancellationToken);
        telemetry.RecordTimerStart();
    }

    private static Task<WorkflowJobRecord> EnqueueTimerStartAsync(
        long definitionId,
        string workflowKey,
        FlowNodeModel node,
        long subscriptionId,
        Guid activationId,
        DateTimeOffset dueAt,
        IWorkflowJobRepository jobs,
        CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(
            new WorkflowJobCreateRecord
            {
                WorkflowDefinitionId = definitionId,
                WorkflowKey = workflowKey,
                TimerSubscriptionId = subscriptionId,
                ActivationId = activationId,
                NodeId = node.Id,
                NodeName = node.Name,
                NodeType = node.Type,
                Kind = WorkflowJobKinds.TimerStart,
                QueueClass = WorkflowJobClasses.Control,
                Phase = WorkflowJobKinds.Timer,
                DueAt = dueAt,
                MaxAttempts = 4,
                ScheduledOccurrenceAt = dueAt
            },
            cancellationToken);

    private static (long Occurrence, DateTimeOffset DueAt)? ResolveInitial(
        TimerDefinitionModel timer,
        DateTimeOffset activatedAt,
        DateTimeOffset now)
    {
        var schedule = WorkflowTimerSchedule.Resolve(timer, activatedAt);
        if (schedule.Interval is null)
        {
            return (0, schedule.FirstOccurrenceAt);
        }

        long occurrence = 0;
        var dueAt = schedule.FirstOccurrenceAt;
        var oldestAllowed = now - MisfireGrace;
        if (dueAt < oldestAllowed)
        {
            var interval = schedule.Interval.Value;
            // Once the oldest occurrence is outside the grace window, skip the
            // complete backlog and schedule the first future nominal
            // occurrence. This avoids a startup catch-up storm.
            var firstFuture = now.AddTicks(1);
            var behindTicks = checked((firstFuture - dueAt).Ticks);
            var skipped = checked(
                behindTicks / interval.Ticks
                + (behindTicks % interval.Ticks == 0 ? 0 : 1));
            occurrence = skipped;
            if (schedule.TotalOccurrences is int finite && occurrence >= finite)
            {
                return null;
            }
            try
            {
                dueAt += TimeSpan.FromTicks(
                    checked(interval.Ticks * skipped));
            }
            catch (OverflowException)
            {
                return null;
            }
        }
        return (occurrence, dueAt);
    }

    private static string ScheduleKind(TimerDefinitionModel timer) =>
        !string.IsNullOrWhiteSpace(timer.TimeDate)
            ? TimerScheduleKinds.Date
            : !string.IsNullOrWhiteSpace(timer.TimeDuration)
                ? TimerScheduleKinds.Duration
                : TimerScheduleKinds.Cycle;

    private static string ScheduleExpression(TimerDefinitionModel timer) =>
        timer.TimeDate ?? timer.TimeDuration ?? timer.TimeCycle
        ?? throw new InvalidOperationException("Timer has no expression.");
}
