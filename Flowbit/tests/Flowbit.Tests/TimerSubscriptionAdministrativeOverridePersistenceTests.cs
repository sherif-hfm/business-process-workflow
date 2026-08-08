using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class TimerSubscriptionAdministrativeOverridePersistenceTests(
    PostgresApiFixture fixture)
{
    [Theory]
    [InlineData(TimerSubscriptionStatuses.Active)]
    [InlineData(TimerSubscriptionStatuses.Paused)]
    public async Task AdministrativeCompletionRequiresExactOccurrenceStatusAndTimestampFence(
        string expectedStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var workflowKey = $"timer-override-{Guid.NewGuid():N}";
        long subscriptionId;
        await using (var setup = fixture.CreateDbContext())
        {
            var definition = new WorkflowDefinitionEntity
            {
                Name = workflowKey,
                WorkflowKey = workflowKey,
                Version = 1,
                IsPublished = true,
                Definition = new WorkflowModel { Id = workflowKey, Name = workflowKey },
                CreatedAt = now
            };
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();
            var instance = new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                Status = "running",
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.WorkflowInstances.Add(instance);
            await setup.SaveChangesAsync();
            var token = new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 1,
                NodeName = "Review",
                NodeType = BpmnFlowNodeTypes.UserTask,
                Status = ExecutionTokenStatuses.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.ExecutionTokens.Add(token);
            await setup.SaveChangesAsync();
            var repository = new TimerSubscriptionRepository(setup);
            var created = await repository.CreateAsync(
                new TimerSubscriptionCreateRecord
                {
                    InstanceId = instance.Id,
                    WorkflowDefinitionId = definition.Id,
                    WorkflowKey = workflowKey,
                    TokenId = token.Id,
                    ActivationId = token.ActivationId,
                    TimerNodeId = 10,
                    TimerNodeName = "Timeout",
                    AttachedToNodeId = 1,
                    ScheduleKind = TimerScheduleKinds.Duration,
                    ScheduleExpression = "PT1H",
                    CancelActivity = true,
                    NextDueAt = now.AddHours(1)
                },
                CancellationToken.None);
            subscriptionId = created.Id;
            if (expectedStatus == TimerSubscriptionStatuses.Paused)
            {
                Assert.True(await repository.PauseAsync(
                    created.Id,
                    created.Occurrence,
                    CancellationToken.None));
            }
        }

        await using var context = fixture.CreateDbContext();
        var subject = new TimerSubscriptionRepository(context);
        var frozen = await subject.GetForUpdateAsync(subscriptionId, CancellationToken.None);
        Assert.NotNull(frozen);
        Assert.Equal(expectedStatus, frozen.Status);
        Assert.False(await subject.CompleteAdministrativeOverrideAsync(
            frozen.Id,
            frozen.Occurrence,
            frozen.Status,
            frozen.UpdatedAt.AddTicks(10),
            CancellationToken.None));
        Assert.True(await subject.CompleteAdministrativeOverrideAsync(
            frozen.Id,
            frozen.Occurrence,
            frozen.Status,
            frozen.UpdatedAt,
            CancellationToken.None));
        await using var verify = fixture.CreateDbContext();
        var completed = await new TimerSubscriptionRepository(verify)
            .GetForUpdateAsync(subscriptionId, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(TimerSubscriptionStatuses.Completed, completed.Status);
        Assert.NotNull(completed.CompletedAt);
    }
}
