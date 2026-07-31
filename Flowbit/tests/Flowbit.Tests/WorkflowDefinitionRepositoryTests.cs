using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class WorkflowDefinitionRepositoryTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task DefaultSwitchRotatesTimerGenerationAndRetiresPreviousTimerStartsAtomically()
    {
        var workflowKey = $"timer-default-{Guid.NewGuid():N}";
        await using var context = fixture.CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = new WorkflowDefinitionRepository(context, cache);
        var first = await repository.AddAsync(
            "First",
            new WorkflowModel { Id = workflowKey, Name = "First" },
            true,
            CancellationToken.None);
        var second = await repository.AddAsync(
            "Second",
            new WorkflowModel { Id = workflowKey, Name = "Second" },
            true,
            CancellationToken.None);
        var firstEntity = await context.WorkflowDefinitions
            .SingleAsync(definition => definition.Id == first.Id);
        var firstGeneration = Assert.IsType<Guid>(firstEntity.DefaultActivationId);
        Assert.NotNull(firstEntity.DefaultActivatedAt);
        var subscription = new TimerSubscriptionEntity
        {
            WorkflowDefinitionId = first.Id,
            WorkflowKey = workflowKey,
            ActivationId = firstGeneration,
            TimerNodeId = 1,
            TimerNodeName = "Scheduled start",
            ScheduleKind = TimerScheduleKinds.Cycle,
            ScheduleExpression = "R/PT1H",
            CancelActivity = true,
            Status = TimerSubscriptionStatuses.Active,
            NextDueAt = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        context.TimerSubscriptions.Add(subscription);
        await context.SaveChangesAsync();
        var occurrence = NewTimerStartJob(first.Id, workflowKey, subscription);
        context.WorkflowJobs.Add(occurrence);
        await context.SaveChangesAsync();

        Assert.True(await repository.SetDefaultAsync(
            second.Id,
            true,
            CancellationToken.None));
        context.ChangeTracker.Clear();

        firstEntity = await context.WorkflowDefinitions.SingleAsync(
            definition => definition.Id == first.Id);
        var secondEntity = await context.WorkflowDefinitions.SingleAsync(
            definition => definition.Id == second.Id);
        var retired = await context.TimerSubscriptions.SingleAsync(
            timer => timer.Id == subscription.Id);
        Assert.False(firstEntity.IsDefault);
        Assert.Null(firstEntity.DefaultActivationId);
        Assert.Null(firstEntity.DefaultActivatedAt);
        Assert.True(secondEntity.IsDefault);
        Assert.NotNull(secondEntity.DefaultActivationId);
        Assert.NotEqual(firstGeneration, secondEntity.DefaultActivationId);
        Assert.NotNull(secondEntity.DefaultActivatedAt);
        Assert.Equal(TimerSubscriptionStatuses.Cancelled, retired.Status);
        Assert.NotNull(retired.CompletedAt);
        var retiredOccurrence = await context.WorkflowJobs.SingleAsync(
            job => job.Id == occurrence.Id);
        Assert.Equal(WorkflowJobStatuses.Cancelled, retiredOccurrence.Status);
        Assert.Equal("cancelled", retiredOccurrence.LastFailureCode);
        Assert.Null(retiredOccurrence.WorkerId);
        Assert.Null(retiredOccurrence.LeaseToken);
        Assert.NotNull(retiredOccurrence.CompletedAt);
    }

    [Fact]
    public async Task DeleteRemovesDefinitionScopedTimerStartRowsBeforeDefinition()
    {
        var workflowKey = $"timer-delete-{Guid.NewGuid():N}";
        await using var context = fixture.CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = new WorkflowDefinitionRepository(context, cache);
        var definition = await repository.AddAsync(
            "Delete timer",
            new WorkflowModel { Id = workflowKey, Name = "Delete timer" },
            true,
            CancellationToken.None);
        var generation = Assert.IsType<Guid>((await context.WorkflowDefinitions
            .SingleAsync(row => row.Id == definition.Id)).DefaultActivationId);
        var now = DateTimeOffset.UtcNow;
        var subscription = new TimerSubscriptionEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            ActivationId = generation,
            TimerNodeId = 1,
            TimerNodeName = "Scheduled start",
            ScheduleKind = TimerScheduleKinds.Duration,
            ScheduleExpression = "PT1H",
            CancelActivity = true,
            Status = TimerSubscriptionStatuses.Active,
            NextDueAt = now.AddHours(1),
            CreatedAt = now,
            UpdatedAt = now
        };
        context.TimerSubscriptions.Add(subscription);
        await context.SaveChangesAsync();
        context.WorkflowJobs.Add(NewTimerStartJob(definition.Id, workflowKey, subscription));
        await context.SaveChangesAsync();

        Assert.True(await repository.DeleteAsync(definition.Id, CancellationToken.None));
        context.ChangeTracker.Clear();

        Assert.False(await context.WorkflowDefinitions.AnyAsync(row => row.Id == definition.Id));
        Assert.False(await context.TimerSubscriptions.AnyAsync(
            row => row.WorkflowDefinitionId == definition.Id));
        Assert.False(await context.WorkflowJobs.AnyAsync(
            row => row.WorkflowDefinitionId == definition.Id));
    }

    [Fact]
    public async Task GetManyAsync_queries_distinct_cache_misses_once_and_returns_safe_clones()
    {
        var suffix = Guid.NewGuid().ToString("N");
        long firstId;
        long secondId;

        await using (var setup = fixture.CreateDbContext())
        {
            var definitions = new[]
            {
                new WorkflowDefinitionEntity
                {
                    Name = $"first-{suffix}",
                    WorkflowKey = $"first-{suffix}",
                    Version = 1,
                    IsPublished = true,
                    Definition = new WorkflowModel { Id = $"first-{suffix}", Name = "First" }
                },
                new WorkflowDefinitionEntity
                {
                    Name = $"second-{suffix}",
                    WorkflowKey = $"second-{suffix}",
                    Version = 1,
                    IsPublished = true,
                    Definition = new WorkflowModel { Id = $"second-{suffix}", Name = "Second" }
                }
            };
            setup.WorkflowDefinitions.AddRange(definitions);
            await setup.SaveChangesAsync();
            firstId = definitions[0].Id;
            secondId = definitions[1].Id;
        }

        var counter = new ReaderCommandCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.DataSource, FlowbitDatabase.ConfigureProvider)
            .AddInterceptors(counter)
            .Options;
        await using var measured = new AppDbContext(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = new WorkflowDefinitionRepository(measured, cache);
        const long missingId = long.MaxValue;

        var firstRead = await repository.GetManyAsync(
            [firstId, firstId, secondId, missingId],
            CancellationToken.None);

        Assert.Equal(1, counter.ReaderCommands);
        Assert.Equal(2, firstRead.Count);
        Assert.DoesNotContain(missingId, firstRead.Keys);

        firstRead[firstId].Definition.Name = "Mutated by caller";
        var secondRead = await repository.GetManyAsync(
            [missingId, secondId, firstId],
            CancellationToken.None);
        var missing = await repository.GetAsync(missingId, CancellationToken.None);

        Assert.Equal(1, counter.ReaderCommands);
        Assert.Equal("First", secondRead[firstId].Definition.Name);
        Assert.Null(missing);
    }

    private sealed class ReaderCommandCounter : DbCommandInterceptor
    {
        public int ReaderCommands { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands++;
            return ValueTask.FromResult(result);
        }
    }

    private static WorkflowJobEntity NewTimerStartJob(
        long definitionId,
        string workflowKey,
        TimerSubscriptionEntity subscription)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowJobEntity
        {
            WorkflowDefinitionId = definitionId,
            WorkflowKey = workflowKey,
            TimerSubscriptionId = subscription.Id,
            ActivationId = subscription.ActivationId,
            NodeId = subscription.TimerNodeId,
            NodeName = subscription.TimerNodeName,
            NodeType = BpmnFlowNodeTypes.TimerStartEvent,
            Kind = WorkflowJobKinds.TimerStart,
            QueueClass = WorkflowJobClasses.Control,
            Phase = WorkflowJobKinds.Timer,
            Status = WorkflowJobStatuses.Queued,
            MaxAttempts = 4,
            FailureHandling = WorkflowJobFailureHandling.BoundaryFirst,
            RetryDelays =
            [
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5)
            ],
            DueAt = subscription.NextDueAt,
            ScheduledOccurrenceAt = subscription.NextDueAt,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
