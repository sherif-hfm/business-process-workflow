using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class DurableParallelFinalizationTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task ExternalActivityFinalizationRefreshesSiblingStateBeforeParallelJoin()
    {
        var workflowKey = $"durable-fresh-finalize-{Guid.NewGuid():N}";
        fixture.ServiceInvocations.Reset();
        var invocationBlock = fixture.ServiceInvocations.BlockNext("/parallel-stale");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            long workflowId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = new WorkflowDefinitionEntity
                {
                    Name = workflowKey,
                    WorkflowKey = workflowKey,
                    Version = 1,
                    IsPublished = true,
                    IsDefault = true,
                    DefaultActivationId = Guid.NewGuid(),
                    DefaultActivatedAt = DateTimeOffset.UtcNow,
                    Definition = CreateWorkflow(workflowKey)
                };
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync(timeout.Token);
                workflowId = definition.Id;
            }

            long instanceId;
            await using (var startScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var engine = startScope.ServiceProvider
                    .GetRequiredService<IWorkflowEngineService>();
                var started = await engine.StartInstanceSlimAsync(
                    workflowId,
                    null,
                    Actor("starter"),
                    null,
                    null,
                    new Dictionary<string, IReadOnlyList<string>>(
                        StringComparer.OrdinalIgnoreCase),
                    timeout.Token);
                instanceId = started.Id;
            }

            long jobId;
            long userTaskId;
            await using (var inspect = fixture.CreateDbContext())
            {
                jobId = await inspect.WorkflowJobs
                    .Where(job =>
                        job.InstanceId == instanceId
                        && job.NodeId == 3
                        && job.Kind == WorkflowJobKinds.AsyncBefore)
                    .Select(job => job.Id)
                    .SingleAsync(timeout.Token);
                userTaskId = await inspect.UserTasks
                    .Where(task =>
                        task.InstanceId == instanceId
                        && task.NodeId == 4
                        && task.Status == UserTaskStatuses.Active)
                    .Select(task => task.Id)
                    .SingleAsync(timeout.Token);
                var branchCounts = await inspect.ExecutionTokens
                    .Where(token => token.InstanceId == instanceId
                                    && token.Status == ExecutionTokenStatuses.Active)
                    .ToDictionaryAsync(
                        token => token.NodeId,
                        token => token.AutomaticActivationCount,
                        timeout.Token);
                Assert.Equal(1, branchCounts[3]);
                Assert.Equal(0, branchCounts[4]);
                // Seed a prior automatic chain on the human branch. The user
                // action below must reset it before the parallel join takes
                // the maximum contributing lineage.
                Assert.Equal(
                    1,
                    await inspect.ExecutionTokens
                        .Where(token => token.InstanceId == instanceId
                                        && token.NodeId == 4
                                        && token.Status == ExecutionTokenStatuses.Active)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(
                                token => token.AutomaticActivationCount,
                                9),
                            timeout.Token));
                await inspect.WorkflowJobs
                    .Where(job => job.Id == jobId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(job => job.Priority, int.MaxValue)
                            .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow),
                        timeout.Token);
            }

            await using var processorScope = fixture.Factory.Services.CreateAsyncScope();
            var repository = processorScope.ServiceProvider
                .GetRequiredService<IWorkflowJobRepository>();
            var leases = await repository.LeaseRunnableAsync(
                new WorkflowJobLeaseRequest(
                    $"fresh-finalize:{Guid.NewGuid():N}",
                    MaxCount: 1,
                    MaxActivityCount: 1,
                    MaxPerInstance: 4,
                    LeaseDuration: TimeSpan.FromMinutes(1)),
                timeout.Token);
            var lease = Assert.Single(leases);
            Assert.Equal(jobId, lease.Job.Id);

            var processor = processorScope.ServiceProvider
                .GetRequiredService<IWorkflowJobProcessor>();
            var processing = processor.ProcessAsync(lease, timeout.Token);
            await invocationBlock.WaitUntilEnteredAsync(timeout.Token);

            try
            {
                await using var actionScope = fixture.Factory.Services.CreateAsyncScope();
                var engine = actionScope.ServiceProvider
                    .GetRequiredService<IWorkflowEngineService>();
                var action = await engine.TakeUserTaskFlowAsync(
                    userTaskId,
                    401,
                    Actor("reviewer"),
                    null,
                    timeout.Token);
                Assert.NotNull(action);
            }
            finally
            {
                invocationBlock.Release();
            }

            await processing.WaitAsync(timeout.Token);

            await using var verification = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                await verification.WorkflowInstances
                    .Where(instance => instance.Id == instanceId)
                    .Select(instance => instance.Status)
                    .SingleAsync(timeout.Token));
            Assert.Empty(await verification.ExecutionTokens
                .Where(token =>
                    token.InstanceId == instanceId
                    && token.Status == ExecutionTokenStatuses.Active)
                .ToListAsync(timeout.Token));
            Assert.Equal(
                1,
                await verification.ExecutionTokens
                    .Where(token => token.InstanceId == instanceId
                                    && token.Status == ExecutionTokenStatuses.Completed)
                    .MaxAsync(token => token.AutomaticActivationCount, timeout.Token));
        }
        finally
        {
            invocationBlock.Release();
            fixture.ServiceInvocations.Reset();
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    private static ActorContext Actor(string user) =>
        new(
            user,
            ["User", "admin"],
            new Dictionary<string, string>());

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowIncidents
            .Where(incident => incident.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowJobs
            .Where(job => job.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.TimerSubscriptions
            .Where(subscription => subscription.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowInstances
            .Where(instance => instance.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions
            .Where(definition => definition.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowJobSnapshots
            .Where(snapshot => !cleanup.WorkflowJobs.Any(job => job.SnapshotId == snapshot.Id))
            .ExecuteDeleteAsync();
    }

    private static WorkflowModel CreateWorkflow(string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Fork",
                    Type = BpmnFlowNodeTypes.ParallelGateway
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "External work",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    AsyncBefore = true,
                    Service = new ServiceTaskModel
                    {
                        Url = "https://tests.local/parallel-stale",
                        Method = "POST"
                    }
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["User"]
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Join",
                    Type = BpmnFlowNodeTypes.ParallelGateway
                },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Done",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel
                {
                    Id = 401,
                    Name = "Complete",
                    SourceRef = 4,
                    TargetRef = 5,
                    Roles = ["User"]
                },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 }
            ]
        };
}
