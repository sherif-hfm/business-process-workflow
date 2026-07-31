using System.Data;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class DurableWorkflowProcessorTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task NonInterruptingTimerReminderLeavesMultiInstanceWaitingAndInvokesWithoutDatabaseLease()
    {
        var workflowKey = $"durable-reminder-{Guid.NewGuid():N}";
        long instanceId;
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
                    Definition = CreateReminderWorkflow(workflowKey)
                };
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                workflowId = definition.Id;
            }

            await using (var startScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var engine = startScope.ServiceProvider
                    .GetRequiredService<IWorkflowEngineService>();
                var started = await engine.StartInstanceSlimAsync(
                    workflowId,
                    null,
                    new ActorContext(
                        "starter",
                        ["User", "admin"],
                        new Dictionary<string, string>()),
                    null,
                    null,
                    new Dictionary<string, IReadOnlyList<string>>(
                        StringComparer.OrdinalIgnoreCase),
                    CancellationToken.None);
                instanceId = started.Id;
                Assert.Equal(2, started.CurrentNodeId);
            }

            long timerJobId;
            DateTimeOffset timerDueAt;
            await using (var inspect = fixture.CreateDbContext())
            {
                var execution = await inspect.MultiInstanceExecutions
                    .SingleAsync(item => item.InstanceId == instanceId);
                Assert.Equal(MultiInstanceExecutionStatuses.Active, execution.Status);
                Assert.Equal(2, execution.TotalCount);
                Assert.Equal(
                    2,
                    await inspect.UserTasks.CountAsync(task =>
                        task.InstanceId == instanceId
                        && task.MultiInstanceExecutionId == execution.Id
                        && task.Status == UserTaskStatuses.Active));

                var timerJob = await inspect.WorkflowJobs
                    .SingleAsync(job =>
                        job.InstanceId == instanceId
                        && job.Kind == WorkflowJobKinds.TimerBoundary);
                timerJobId = timerJob.Id;
                timerDueAt = timerJob.DueAt;
            }

            var delay = timerDueAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(75);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }
            await PromoteAndProcessAsync(
                timerJobId,
                WorkflowJobClasses.Control);

            long reminderJobId;
            await using (var afterTimer = fixture.CreateDbContext())
            {
                var hostToken = await afterTimer.ExecutionTokens
                    .SingleAsync(token =>
                        token.InstanceId == instanceId
                        && token.NodeId == 2
                        && token.Status == ExecutionTokenStatuses.Active);
                Assert.Null(hostToken.WaitState);
                Assert.Equal(
                    2,
                    await afterTimer.UserTasks.CountAsync(task =>
                        task.InstanceId == instanceId
                        && task.Status == UserTaskStatuses.Active));

                var reminderJob = await afterTimer.WorkflowJobs
                    .SingleAsync(job =>
                        job.InstanceId == instanceId
                        && job.NodeId == 4
                        && job.Kind == WorkflowJobKinds.AsyncBefore
                        && job.Status == WorkflowJobStatuses.Queued);
                reminderJobId = reminderJob.Id;
            }

            fixture.ServiceInvocations.Reset();
            await PromoteAndProcessAsync(
                reminderJobId,
                WorkflowJobClasses.Activity);

            var observation = Assert.Single(fixture.ServiceInvocations.Snapshot());
            Assert.False(observation.HasTransaction);
            Assert.Equal(ConnectionState.Closed, observation.ConnectionState);

            await using var completed = fixture.CreateDbContext();
            var instance = await completed.WorkflowInstances
                .SingleAsync(item => item.Id == instanceId);
            Assert.Equal(WorkflowInstanceStatuses.Running, instance.Status);
            Assert.Equal(
                2,
                await completed.UserTasks.CountAsync(task =>
                    task.InstanceId == instanceId
                    && task.Status == UserTaskStatuses.Active));
            var activeTokens = await completed.ExecutionTokens
                .Where(token =>
                    token.InstanceId == instanceId
                    && token.Status == ExecutionTokenStatuses.Active)
                .ToListAsync();
            var activeHost = Assert.Single(activeTokens);
            Assert.Equal(2, activeHost.NodeId);
            Assert.Equal(
                WorkflowJobStatuses.Completed,
                await completed.WorkflowJobs
                    .Where(job => job.Id == reminderJobId)
                    .Select(job => job.Status)
                    .SingleAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task TimerCatchArmsAttachedBoundaryAndInterruptingBoundaryCancelsPrimaryCatch()
    {
        var workflowKey = $"timer-catch-boundary-{Guid.NewGuid():N}";
        try
        {
            var instanceId = await InsertAndStartAsync(
                workflowKey,
                CreateTimerCatchBoundaryWorkflow(workflowKey));

            long boundaryJobId;
            long catchJobId;
            await using (var inspect = fixture.CreateDbContext())
            {
                var jobs = await inspect.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId)
                    .OrderBy(job => job.NodeId)
                    .ToListAsync();
                var catchJob = Assert.Single(jobs, job => job.Kind == WorkflowJobKinds.Timer);
                var boundaryJob = Assert.Single(
                    jobs,
                    job => job.Kind == WorkflowJobKinds.TimerBoundary);
                catchJobId = catchJob.Id;
                boundaryJobId = boundaryJob.Id;

                var subscriptions = await inspect.TimerSubscriptions
                    .Where(subscription => subscription.InstanceId == instanceId)
                    .OrderBy(subscription => subscription.TimerNodeId)
                    .ToListAsync();
                Assert.Equal(2, subscriptions.Count);
                Assert.All(
                    subscriptions,
                    subscription => Assert.Equal(
                        TimerSubscriptionStatuses.Active,
                        subscription.Status));
            }

            await PromoteAndProcessAsync(boundaryJobId, WorkflowJobClasses.Control);

            await using var completed = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                await completed.WorkflowInstances
                    .Where(instance => instance.Id == instanceId)
                    .Select(instance => instance.Status)
                    .SingleAsync());
            Assert.Equal(
                WorkflowJobStatuses.Cancelled,
                await completed.WorkflowJobs
                    .Where(job => job.Id == catchJobId)
                    .Select(job => job.Status)
                    .SingleAsync());
            var terminalSubscriptions = await completed.TimerSubscriptions
                .Where(subscription => subscription.InstanceId == instanceId)
                .OrderBy(subscription => subscription.TimerNodeId)
                .ToListAsync();
            Assert.Equal(
                TimerSubscriptionStatuses.Cancelled,
                terminalSubscriptions.Single(subscription => subscription.TimerNodeId == 2).Status);
            Assert.Equal(
                TimerSubscriptionStatuses.Completed,
                terminalSubscriptions.Single(subscription => subscription.TimerNodeId == 3).Status);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task OmittedRetryDelaysUseDefaultsWhileExplicitEmptyDisablesRetries()
    {
        var omittedKey = $"retry-default-{Guid.NewGuid():N}";
        var emptyKey = $"retry-empty-{Guid.NewGuid():N}";
        try
        {
            var omittedInstanceId = await InsertAndStartAsync(
                omittedKey,
                CreateAsyncServiceWorkflow(
                    omittedKey,
                    new JobPolicyModel
                    {
                        FailureHandling = JobFailureHandling.RetryFirst
                    }));
            var emptyInstanceId = await InsertAndStartAsync(
                emptyKey,
                CreateAsyncServiceWorkflow(
                    emptyKey,
                    new JobPolicyModel
                    {
                        FailureHandling = JobFailureHandling.RetryFirst,
                        RetryDelays = []
                    }));

            await using var inspect = fixture.CreateDbContext();
            var omitted = await inspect.WorkflowJobs.SingleAsync(
                job => job.InstanceId == omittedInstanceId);
            Assert.Equal(4, omitted.MaxAttempts);
            Assert.Equal(
                [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)],
                omitted.RetryDelays);

            var explicitEmpty = await inspect.WorkflowJobs.SingleAsync(
                job => job.InstanceId == emptyInstanceId);
            Assert.Equal(1, explicitEmpty.MaxAttempts);
            Assert.Empty(explicitEmpty.RetryDelays);
        }
        finally
        {
            await DeleteWorkflowAsync(omittedKey);
            await DeleteWorkflowAsync(emptyKey);
        }
    }

    [Fact]
    public async Task MissingStagedSnapshotCreatesIncidentAndManualRetryCanRecover()
    {
        var workflowKey = $"missing-stage-{Guid.NewGuid():N}";
        fixture.ServiceInvocations.Reset();
        try
        {
            var instanceId = await InsertAndStartAsync(
                workflowKey,
                CreateAsyncServiceWorkflow(workflowKey, new JobPolicyModel { RetryDelays = [] }));
            long jobId;
            await using (var inspect = fixture.CreateDbContext())
            {
                jobId = await inspect.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId)
                    .Select(job => job.Id)
                    .SingleAsync();
            }

            var block = fixture.ServiceInvocations.BlockNext("/typed-output-success");
            var processing = PromoteAndProcessAsync(jobId, WorkflowJobClasses.Activity);
            await block.WaitUntilEnteredAsync(CancellationToken.None);
            try
            {
                await using var corrupt = fixture.CreateDbContext();
                var job = await corrupt.WorkflowJobs.SingleAsync(item => item.Id == jobId);
                var snapshotId = Assert.IsType<long>(job.SnapshotId);
                job.SnapshotId = null;
                await corrupt.SaveChangesAsync();
                Assert.Equal(
                    1,
                    await corrupt.WorkflowJobSnapshots
                        .Where(snapshot => snapshot.Id == snapshotId)
                        .ExecuteDeleteAsync());
            }
            finally
            {
                block.Release();
            }
            await processing;

            long incidentId;
            await using (var incidentState = fixture.CreateDbContext())
            {
                var job = await incidentState.WorkflowJobs.SingleAsync(item => item.Id == jobId);
                Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
                var incident = await incidentState.WorkflowIncidents.SingleAsync(
                    item => item.JobId == jobId && item.Status == WorkflowIncidentStatuses.Open);
                incidentId = incident.Id;
                Assert.Equal("job_invariant_violation", incident.Type);

                var token = await incidentState.ExecutionTokens.SingleAsync(
                    item => item.Id == job.TokenId);
                Assert.Equal(ExecutionTokenWaitStates.AsyncBefore, token.WaitState);
                Assert.Equal(jobId, token.WaitingJobId);
            }

            await RetryIncidentAsync(incidentId);
            await PromoteAndProcessAsync(jobId, WorkflowJobClasses.Activity);

            await using var recovered = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                await recovered.WorkflowInstances
                    .Where(instance => instance.Id == instanceId)
                    .Select(instance => instance.Status)
                    .SingleAsync());
            Assert.Equal(
                WorkflowJobStatuses.Completed,
                await recovered.WorkflowJobs
                    .Where(job => job.Id == jobId)
                    .Select(job => job.Status)
                    .SingleAsync());
        }
        finally
        {
            fixture.ServiceInvocations.Reset();
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task OutputConflictRetryDoesNotRecreateCompletedOneShotBoundary()
    {
        var workflowKey = $"boundary-output-retry-{Guid.NewGuid():N}";
        fixture.ServiceInvocations.Reset();
        try
        {
            var instanceId = await InsertAndStartAsync(
                workflowKey,
                CreateAsyncServiceWorkflow(
                    workflowKey,
                    new JobPolicyModel { RetryDelays = [] },
                    includeBoundary: true));
            long activityJobId;
            await using (var inspect = fixture.CreateDbContext())
            {
                activityJobId = await inspect.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId)
                    .Select(job => job.Id)
                    .SingleAsync();
            }

            var block = fixture.ServiceInvocations.BlockNext("/typed-output-success");
            var activityProcessing = PromoteAndProcessAsync(
                activityJobId,
                WorkflowJobClasses.Activity);
            await block.WaitUntilEnteredAsync(CancellationToken.None);
            try
            {
                long boundaryJobId;
                await using (var staged = fixture.CreateDbContext())
                {
                    boundaryJobId = await staged.WorkflowJobs
                        .Where(job =>
                            job.InstanceId == instanceId
                            && job.Kind == WorkflowJobKinds.TimerBoundary)
                        .Select(job => job.Id)
                        .SingleAsync();
                }
                await PromoteAndProcessAsync(boundaryJobId, WorkflowJobClasses.Control);

                await using var concurrentWrite = fixture.CreateDbContext();
                concurrentWrite.InstanceVariables.Add(new InstanceVariableEntity
                {
                    InstanceId = instanceId,
                    VariableName = "decision",
                    SourceActionId = 99,
                    ValueJson = JsonDocument.Parse("\"concurrent\""),
                    SetBy = "parallel-branch",
                    SetAt = DateTimeOffset.UtcNow
                });
                await concurrentWrite.SaveChangesAsync();
            }
            finally
            {
                block.Release();
            }
            await activityProcessing;

            long incidentId;
            await using (var conflicted = fixture.CreateDbContext())
            {
                var incident = await conflicted.WorkflowIncidents.SingleAsync(
                    item => item.JobId == activityJobId
                            && item.Status == WorkflowIncidentStatuses.Open);
                incidentId = incident.Id;
                Assert.Equal("output_version_conflict", incident.Type);
                var boundary = Assert.Single(await conflicted.TimerSubscriptions
                    .Where(subscription =>
                        subscription.InstanceId == instanceId
                        && subscription.TimerNodeId == 3)
                    .ToListAsync());
                Assert.Equal(TimerSubscriptionStatuses.Completed, boundary.Status);
            }

            await RetryIncidentAsync(incidentId);
            await PromoteAndProcessAsync(activityJobId, WorkflowJobClasses.Activity);

            await using var completed = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                await completed.WorkflowInstances
                    .Where(instance => instance.Id == instanceId)
                    .Select(instance => instance.Status)
                    .SingleAsync());
            Assert.Single(await completed.TimerSubscriptions
                .Where(subscription =>
                    subscription.InstanceId == instanceId
                    && subscription.TimerNodeId == 3)
                .ToListAsync());
        }
        finally
        {
            fixture.ServiceInvocations.Reset();
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    private async Task<long> InsertAndStartAsync(
        string workflowKey,
        WorkflowModel definition)
    {
        long workflowId;
        await using (var setup = fixture.CreateDbContext())
        {
            var entity = new WorkflowDefinitionEntity
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
            setup.WorkflowDefinitions.Add(entity);
            await setup.SaveChangesAsync();
            workflowId = entity.Id;
        }

        await using var startScope = fixture.Factory.Services.CreateAsyncScope();
        var engine = startScope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
        var started = await engine.StartInstanceSlimAsync(
            workflowId,
            null,
            new ActorContext(
                "starter",
                ["User", "admin"],
                new Dictionary<string, string>()),
            null,
            null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        return started.Id;
    }

    private async Task RetryIncidentAsync(long incidentId)
    {
        await using var retryScope = fixture.Factory.Services.CreateAsyncScope();
        var repository = retryScope.ServiceProvider
            .GetRequiredService<IWorkflowJobRepository>();
        Assert.NotNull(await repository.RetryIncidentAsync(
            incidentId,
            "test-admin",
            DateTimeOffset.UtcNow,
            CancellationToken.None));
    }

    private async Task PromoteAndProcessAsync(long jobId, string queueClass)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            var changed = await promote.WorkflowJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, 1_000_000)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow));
            Assert.Equal(1, changed);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider
            .GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"processor-test:{Guid.NewGuid():N}",
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

    private static WorkflowModel CreateTimerCatchBoundaryWorkflow(string workflowKey) =>
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
                    Name = "Wait for deadline",
                    Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT2H" }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Escalation deadline",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = true,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Caught normally",
                    Type = BpmnFlowNodeTypes.EndEvent
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Escalated",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 }
            ]
        };

    private static WorkflowModel CreateAsyncServiceWorkflow(
        string workflowKey,
        JobPolicyModel job,
        bool includeBoundary = false)
    {
        var nodes = new List<FlowNodeModel>
        {
            new()
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent
            },
            new()
            {
                Id = 2,
                Name = "Fetch decision",
                Type = BpmnFlowNodeTypes.ServiceTask,
                AsyncBefore = true,
                Job = job,
                Service = new ServiceTaskModel
                {
                    Url = "https://tests.local/typed-output-success",
                    Method = "GET",
                    OutputMappings =
                    [
                        new ServiceOutputMappingModel
                        {
                            Variable = "decision",
                            Path = "result.decision",
                            Required = true,
                            DataType = WorkflowVariableTypes.String,
                            IsArray = false
                        }
                    ]
                }
            },
            new()
            {
                Id = 4,
                Name = "Service complete",
                Type = BpmnFlowNodeTypes.EndEvent
            }
        };
        var flows = new List<SequenceFlowModel>
        {
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, SourceRef = 2, TargetRef = 4 }
        };
        if (includeBoundary)
        {
            nodes.AddRange(
            [
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "One-shot reminder",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = false,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Reminder complete",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ]);
            flows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 });
        }

        return new WorkflowModel
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "decision",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement("initial")
                }
            ],
            FlowNodes = nodes,
            SequenceFlows = flows
        };
    }

    private static WorkflowModel CreateReminderWorkflow(string workflowKey) =>
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
                    DefaultValue = JsonSerializer.SerializeToElement(
                        Array.Empty<object>())
                }
            ],
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
                    Name = "Parallel review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["User"],
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "2",
                        CompletionEvaluation =
                            MultiInstanceCompletionEvaluations.AfterAll,
                        ResultVariable = "reviewResults"
                    }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Reminder due",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = false,
                    Timer = new TimerDefinitionModel
                    {
                        TimeDuration = "PT0.2S"
                    }
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Send reminder",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    Service = new ServiceTaskModel
                    {
                        Url = "https://tests.local/send-reminder",
                        Method = "POST",
                        Body = """{"kind":"reminder"}"""
                    }
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Reminder sent",
                    Type = BpmnFlowNodeTypes.EndEvent
                },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Reviews complete",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel
                {
                    Id = 101,
                    SourceRef = 1,
                    TargetRef = 2
                },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 6,
                    Roles = ["User"],
                    CompletionCondition = "CountFlow(201) >= 2",
                    CompletionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 202,
                    Name = "No outcome",
                    SourceRef = 2,
                    TargetRef = 6,
                    IsDefault = true,
                    IsSelectable = false
                },
                new SequenceFlowModel
                {
                    Id = 301,
                    SourceRef = 3,
                    TargetRef = 4
                },
                new SequenceFlowModel
                {
                    Id = 401,
                    SourceRef = 4,
                    TargetRef = 5
                }
            ]
        };
}
