extern alias FlowbitWorker;

using System.Reflection;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TimerStartReconciliationService = FlowbitWorker::Flowbit.Worker.TimerStartReconciliationService;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class DurableAutomaticLoopGuardResetIntegrationTests(PostgresApiFixture fixture)
{
    [Fact]
    public Task MessageDeliveryStartsTheNextAutomaticChainAtOne() =>
        RunWithLimitOneAsync(
            $"automatic-reset-message-{Guid.NewGuid():N}",
            async workflowKey =>
            {
                var instanceId = await InsertAndStartAsync(
                    workflowKey,
                    CreateMessageResetWorkflow(workflowKey));
                await SeedActiveTokenCountAsync(instanceId, expectedNodeId: 2, count: 1);

                await using (var deliveryScope = fixture.Factory.Services.CreateAsyncScope())
                {
                    var engine = deliveryScope.ServiceProvider
                        .GetRequiredService<IWorkflowEngineService>();
                    var headers = new Dictionary<string, IReadOnlyList<string>>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-Client-Id"] = ["tests-client"],
                        ["X-Client-Secret"] = ["tests-secret"],
                        ["X-Correlation"] = ["accepted"]
                    };
                    var ack = await engine.DeliverMessageAsync(
                        instanceId,
                        new IncomingMessage(
                            "tests-client",
                            "tests-secret",
                            headers,
                            null,
                            Actor("untrusted-transport-actor")),
                        CancellationToken.None);
                    Assert.NotNull(ack);
                }

                await AssertSingleResetActivityJobAsync(instanceId);
            });

    [Fact]
    public Task IntermediateTimerCatchStartsTheNextAutomaticChainAtOne() =>
        RunWithLimitOneAsync(
            $"automatic-reset-timer-catch-{Guid.NewGuid():N}",
            async workflowKey =>
            {
                var instanceId = await InsertAndStartAsync(
                    workflowKey,
                    CreateTimerCatchResetWorkflow(workflowKey));
                await SeedActiveTokenCountAsync(instanceId, expectedNodeId: 2, count: 1);

                long timerJobId;
                await using (var queued = fixture.CreateDbContext())
                {
                    timerJobId = await queued.WorkflowJobs
                        .Where(job =>
                            job.InstanceId == instanceId
                            && job.Kind == WorkflowJobKinds.Timer)
                        .Select(job => job.Id)
                        .SingleAsync();
                }

                await PromoteAndProcessAsync(timerJobId, WorkflowJobClasses.Control);
                await AssertSingleResetActivityJobAsync(instanceId);
            });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task TimerBoundaryStartsItsContinuationAtOne(bool cancelActivity) =>
        RunWithLimitOneAsync(
            $"automatic-reset-boundary-{cancelActivity}-{Guid.NewGuid():N}",
            async workflowKey =>
            {
                var instanceId = await InsertAndStartAsync(
                    workflowKey,
                    CreateTimerBoundaryResetWorkflow(workflowKey, cancelActivity));
                await SeedActiveTokenCountAsync(instanceId, expectedNodeId: 2, count: 1);

                long boundaryJobId;
                await using (var queued = fixture.CreateDbContext())
                {
                    boundaryJobId = await queued.WorkflowJobs
                        .Where(job =>
                            job.InstanceId == instanceId
                            && job.Kind == WorkflowJobKinds.TimerBoundary)
                        .Select(job => job.Id)
                        .SingleAsync();
                }

                await PromoteAndProcessAsync(boundaryJobId, WorkflowJobClasses.Control);
                await AssertSingleResetActivityJobAsync(instanceId);

                await using var state = fixture.CreateDbContext();
                Assert.Equal(
                    cancelActivity ? 0 : 1,
                    await state.UserTasks.CountAsync(task =>
                        task.InstanceId == instanceId
                        && task.Status == UserTaskStatuses.Active));
            });

    [Fact]
    public Task TimerStartCreatesItsFirstAutomaticJobAtOne() =>
        RunWithLimitOneAsync(
            $"automatic-reset-timer-start-{Guid.NewGuid():N}",
            async workflowKey =>
            {
                await AddPublishedTimerStartDefinitionAsync(
                    CreateTimerStartResetWorkflow(workflowKey));
                await ReconcileTimerStartsAsync();

                long timerStartJobId;
                await using (var scheduled = fixture.CreateDbContext())
                {
                    timerStartJobId = await scheduled.WorkflowJobs
                        .Where(job =>
                            job.WorkflowKey == workflowKey
                            && job.Kind == WorkflowJobKinds.TimerStart)
                        .Select(job => job.Id)
                        .SingleAsync();
                }

                await PromoteAndProcessAsync(timerStartJobId, WorkflowJobClasses.Control);

                await using var state = fixture.CreateDbContext();
                var instanceId = await state.WorkflowInstances
                    .Where(instance => instance.WorkflowKey == workflowKey)
                    .Select(instance => instance.Id)
                    .SingleAsync();
                await AssertSingleResetActivityJobAsync(instanceId);
            });

    [Fact]
    public Task MultiInstanceParentCompletionStartsTheNextAutomaticChainAtOne() =>
        RunWithLimitOneAsync(
            $"automatic-reset-multi-instance-{Guid.NewGuid():N}",
            async workflowKey =>
            {
                var instanceId = await InsertAndStartAsync(
                    workflowKey,
                    CreateMultiInstanceResetWorkflow(workflowKey));
                await SeedActiveTokenCountAsync(instanceId, expectedNodeId: 2, count: 1);

                long userTaskId;
                await using (var waiting = fixture.CreateDbContext())
                {
                    userTaskId = await waiting.UserTasks
                        .Where(task =>
                            task.InstanceId == instanceId
                            && task.NodeId == 2
                            && task.Status == UserTaskStatuses.Active)
                        .Select(task => task.Id)
                        .SingleAsync();
                }

                await using (var actionScope = fixture.Factory.Services.CreateAsyncScope())
                {
                    var engine = actionScope.ServiceProvider
                        .GetRequiredService<IWorkflowEngineService>();
                    var action = await engine.TakeUserTaskFlowAsync(
                        userTaskId,
                        201,
                        Actor("reviewer"),
                        null,
                        CancellationToken.None);
                    Assert.NotNull(action);
                }

                await AssertSingleResetActivityJobAsync(instanceId);
            });

    private async Task RunWithLimitOneAsync(
        string workflowKey,
        Func<string, Task> test)
    {
        EngineSettingRecord? previousSetting = null;
        try
        {
            await using (var settingsScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var settings = settingsScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsRepository>();
                previousSetting = await settings.GetByKeyAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    CancellationToken.None);
                await settings.SetAsync(
                    WorkflowAutomaticActivationGuard.SettingKey,
                    "1",
                    CancellationToken.None);
            }

            await test(workflowKey);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
            await RestoreSettingAsync(previousSetting);
        }
    }

    private async Task<long> InsertAndStartAsync(
        string workflowKey,
        WorkflowModel definition)
    {
        long workflowId;
        await using (var setup = fixture.CreateDbContext())
        {
            var entity = NewPublishedDefinition(workflowKey, definition);
            setup.WorkflowDefinitions.Add(entity);
            await setup.SaveChangesAsync();
            workflowId = entity.Id;
        }

        await using var startScope = fixture.Factory.Services.CreateAsyncScope();
        var engine = startScope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
        var started = await engine.StartInstanceSlimAsync(
            workflowId,
            null,
            Actor("starter"),
            null,
            null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        return started.Id;
    }

    private async Task AddPublishedTimerStartDefinitionAsync(WorkflowModel definition)
    {
        await using var context = fixture.CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var definitions = new WorkflowDefinitionRepository(context, cache);
        await definitions.AddAsync(
            definition.Name,
            definition,
            true,
            CancellationToken.None);
    }

    private async Task ReconcileTimerStartsAsync()
    {
        var options = new WorkerOptions { TimerStartReconcileBatchSize = 1000 };
        using var telemetry = new WorkerTelemetry();
        var service = new TimerStartReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            telemetry,
            NullLogger<TimerStartReconciliationService>.Instance);
        var reconcile = typeof(TimerStartReconciliationService).GetMethod(
            "ReconcileAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReconcileAsync was not found.");
        var invocation = reconcile.Invoke(service, [CancellationToken.None]);
        await Assert.IsAssignableFrom<Task>(invocation);
    }

    private async Task SeedActiveTokenCountAsync(
        long instanceId,
        int expectedNodeId,
        int count)
    {
        await using var setup = fixture.CreateDbContext();
        var changed = await setup.ExecutionTokens
            .Where(token =>
                token.InstanceId == instanceId
                && token.NodeId == expectedNodeId
                && token.Status == ExecutionTokenStatuses.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.AutomaticActivationCount, count));
        Assert.Equal(1, changed);
    }

    private async Task AssertSingleResetActivityJobAsync(long instanceId)
    {
        await using var state = fixture.CreateDbContext();
        var activityJobs = await state.WorkflowJobs
            .Where(job =>
                job.InstanceId == instanceId
                && job.QueueClass == WorkflowJobClasses.Activity)
            .ToListAsync();
        var job = Assert.Single(activityJobs);
        Assert.Equal(WorkflowJobKinds.AsyncBefore, job.Kind);
        Assert.Equal(WorkflowJobStatuses.Queued, job.Status);
        Assert.Equal(1, job.AutomaticActivationCount);
        Assert.Equal(0, job.AttemptCount);
        Assert.Empty(await state.WorkflowIncidents
            .Where(incident => incident.InstanceId == instanceId)
            .ToListAsync());

        var token = await state.ExecutionTokens.SingleAsync(token =>
            token.Id == job.TokenId
            && token.Status == ExecutionTokenStatuses.Active);
        Assert.Equal(1, token.AutomaticActivationCount);
        Assert.Equal(ExecutionTokenWaitStates.AsyncBefore, token.WaitState);
        Assert.Equal(job.Id, token.WaitingJobId);
    }

    private async Task PromoteAndProcessAsync(long jobId, string queueClass)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            var changed = await promote.WorkflowJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, int.MaxValue)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow));
            Assert.Equal(1, changed);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"automatic-reset-test:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: queueClass == WorkflowJobClasses.Activity ? 1 : 0,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        Assert.Equal(queueClass, lease.Job.QueueClass);

        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
        await processor.ProcessAsync(lease, CancellationToken.None);
    }

    private async Task RestoreSettingAsync(EngineSettingRecord? previousSetting)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IEngineSettingsRepository>();
        if (previousSetting is null)
        {
            await settings.DeleteAsync(
                WorkflowAutomaticActivationGuard.SettingKey,
                CancellationToken.None);
            return;
        }

        await settings.SetAsync(
            WorkflowAutomaticActivationGuard.SettingKey,
            previousSetting.Value,
            CancellationToken.None);
    }

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

    private static ActorContext Actor(string user) =>
        new(
            user,
            ["User", "admin"],
            new Dictionary<string, string>());

    private static WorkflowDefinitionEntity NewPublishedDefinition(
        string workflowKey,
        WorkflowModel definition) =>
        new()
        {
            Name = workflowKey,
            WorkflowKey = workflowKey,
            Version = 1,
            IsPublished = true,
            IsDefault = true,
            DefaultActivationId = Guid.NewGuid(),
            DefaultActivatedAt = DateTimeOffset.UtcNow,
            Definition = definition
        };

    private static FlowNodeModel ResetService() =>
        new()
        {
            Id = 3,
            Name = "First automatic activity after trigger",
            Type = BpmnFlowNodeTypes.ServiceTask,
            AsyncBefore = true,
            Job = new JobPolicyModel { RetryDelays = [] },
            Service = new ServiceTaskModel
            {
                Url = "https://tests.local/send-reminder",
                Method = "POST",
                Body = """{"kind":"reset-verification"}"""
            }
        };

    private static WorkflowModel CreateMessageResetWorkflow(string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Wait for message",
                    Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "tests-client",
                        ClientSecret = "tests-secret",
                        HeaderName = "X-Correlation",
                        HeaderValue = "accepted"
                    }
                },
                ResetService(),
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };

    private static WorkflowModel CreateTimerCatchResetWorkflow(string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Wait for timer",
                    Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                },
                ResetService(),
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };

    private static WorkflowModel CreateTimerBoundaryResetWorkflow(
        string workflowKey,
        bool cancelActivity) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Human wait",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["User"]
                },
                ResetService(),
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Boundary timer",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = cancelActivity,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                },
                new FlowNodeModel { Id = 5, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Complete",
                    SourceRef = 2,
                    TargetRef = 5,
                    Roles = ["User"]
                },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 }
            ]
        };

    private static WorkflowModel CreateTimerStartResetWorkflow(string workflowKey) =>
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
                    Name = "Scheduled start",
                    Type = BpmnFlowNodeTypes.TimerStartEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "First automatic activity after trigger",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    AsyncBefore = true,
                    Job = new JobPolicyModel { RetryDelays = [] },
                    Service = new ServiceTaskModel
                    {
                        Url = "https://tests.local/send-reminder",
                        Method = "POST",
                        Body = """{"kind":"timer-start-reset"}"""
                    }
                },
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };

    private static WorkflowModel CreateMultiInstanceResetWorkflow(string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "reviewResults",
                    DataType = WorkflowVariableTypes.Json,
                    DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "One review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["User"],
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "1",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterEach,
                        ResultVariable = "reviewResults"
                    }
                },
                ResetService(),
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["User"],
                    CompletionCondition = "CountFlow(201) >= 1",
                    CompletionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 202,
                    Name = "No outcome",
                    SourceRef = 2,
                    TargetRef = 3,
                    IsDefault = true,
                    IsSelectable = false
                },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };
}
