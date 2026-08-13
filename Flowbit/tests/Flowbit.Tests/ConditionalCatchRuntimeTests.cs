using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class ConditionalCatchRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Immediate_true_atomic_condition_completes_in_start_transaction()
    {
        var workflowKey = $"conditional-atomic-start-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                approvedDefault: true);

            var started = await StartAsync(workflowId, approved: true);

            Assert.Equal(WorkflowInstanceStatuses.Completed, started.Status);
            Assert.Equal(3, started.CurrentNodeId);
            await using var db = fixture.CreateDbContext();
            Assert.Empty(await db.WorkflowJobs
                .Where(job => job.InstanceId == started.Id
                    && job.Kind == WorkflowJobKinds.ConditionalWake)
                .ToListAsync());
            Assert.Equal(
                NodeExecutionCompletionReasons.ConditionalTriggered,
                (await db.NodeExecutions.SingleAsync(execution =>
                    execution.InstanceId == started.Id
                    && execution.NodeId == 2)).CompletionReason);
            Assert.Single(await db.InstanceHistory
                .Where(item => item.InstanceId == started.Id
                    && item.Note == InstanceHistoryNotes.ConditionalTriggered)
                .ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task False_atomic_condition_resumes_after_administrative_variable_update()
    {
        var workflowKey = $"conditional-atomic-update-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic);
            var started = await StartAsync(workflowId, approved: false);
            await AssertWaitingWithoutJobAsync(started.Id);

            _ = await PatchVariableAsync(started.Id, "approved", true);

            await using var db = fixture.CreateDbContext();
            var instance = await db.WorkflowInstances.SingleAsync(item => item.Id == started.Id);
            Assert.Equal(WorkflowInstanceStatuses.Completed, instance.Status);
            Assert.Empty(await db.WorkflowJobs
                .Where(job => job.InstanceId == started.Id
                    && job.Kind == WorkflowJobKinds.ConditionalWake)
                .ToListAsync());
            var history = await db.InstanceHistory.SingleAsync(item =>
                item.InstanceId == started.Id
                && item.Note == InstanceHistoryNotes.ConditionalTriggered);
            Assert.Equal("operator", history.PerformedBy);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Durable_wake_routes_latched_flow_even_if_condition_becomes_false()
    {
        var workflowKey = $"conditional-durable-latch-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync);
            var started = await StartAsync(workflowId, approved: false);

            _ = await PatchVariableAsync(started.Id, "approved", true);
            long jobId;
            await using (var latched = fixture.CreateDbContext())
            {
                var token = await latched.ExecutionTokens.SingleAsync(item =>
                    item.InstanceId == started.Id
                    && item.Status == ExecutionTokenStatuses.Active);
                var job = await latched.WorkflowJobs.SingleAsync(item =>
                    item.InstanceId == started.Id
                    && item.Kind == WorkflowJobKinds.ConditionalWake);
                jobId = job.Id;
                Assert.Equal(ExecutionTokenWaitStates.ConditionalWake, token.WaitState);
                Assert.Equal(job.Id, token.WaitingJobId);
                Assert.Equal(WorkflowJobStatuses.Queued, job.Status);
                Assert.Single(await latched.InstanceHistory
                    .Where(item => item.InstanceId == started.Id
                        && item.Note == InstanceHistoryNotes.ConditionalLatched)
                    .ToListAsync());
            }

            // Truth is edge-latched by the writer. This later write must not
            // withdraw the wake or cause the worker to re-evaluate the predicate.
            _ = await PatchVariableAsync(started.Id, "approved", false);
            await using (var beforeWorker = fixture.CreateDbContext())
            {
                Assert.Equal(
                    jobId,
                    (await beforeWorker.ExecutionTokens.SingleAsync(item =>
                        item.InstanceId == started.Id
                        && item.Status == ExecutionTokenStatuses.Active)).WaitingJobId);
                Assert.Equal(
                    1,
                    await beforeWorker.WorkflowJobs.CountAsync(item =>
                        item.InstanceId == started.Id
                        && item.Kind == WorkflowJobKinds.ConditionalWake));
            }

            var lease = await LeaseJobAsync(jobId);
            await ProcessLeaseAsync(lease);

            await using var completed = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await completed.WorkflowInstances.SingleAsync(item =>
                    item.Id == started.Id)).Status);
            Assert.Equal(
                WorkflowJobStatuses.Completed,
                (await completed.WorkflowJobs.SingleAsync(item => item.Id == jobId)).Status);
            Assert.Equal(
                NodeExecutionCompletionReasons.ConditionalTriggered,
                (await completed.NodeExecutions.SingleAsync(execution =>
                    execution.InstanceId == started.Id
                    && execution.NodeId == 2)).CompletionReason);
            Assert.Single(await completed.InstanceHistory
                .Where(item => item.InstanceId == started.Id
                    && item.Note == InstanceHistoryNotes.ConditionalTriggered)
                .ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Irrelevant_variable_write_does_not_evaluate_waiting_condition()
    {
        var workflowKey = $"conditional-irrelevant-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync);
            var started = await StartAsync(workflowId, approved: false);

            using var evaluations = new ConditionalEvaluationProbe();
            fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
            _ = await PatchVariableAsync(started.Id, "noise", true);

            Assert.Equal(0, evaluations.Count);
            Assert.Single(
                fixture.CommandCounter.ReaderCommandTexts,
                command => MentionsTable(command, "execution_tokens"));
            Assert.Single(
                fixture.CommandCounter.ReaderCommandTexts,
                command => MentionsTable(
                    command,
                    "instance_variable_current_values"));

            await AssertWaitingWithoutJobAsync(started.Id);
            await using var db = fixture.CreateDbContext();
            Assert.DoesNotContain(
                await db.InstanceHistory
                    .Where(item => item.InstanceId == started.Id)
                    .ToListAsync(),
                item => item.Note is InstanceHistoryNotes.ConditionalLatched
                    or InstanceHistoryNotes.ConditionalTriggered);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Observable_write_query_budget_does_not_grow_with_unrelated_instances_or_history()
    {
        const int unrelatedInstanceCount = 12;
        const int historyRowsPerInstance = 50;
        var workflowKey = $"conditional-query-budget-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic);
            var target = await StartAsync(workflowId, approved: false);

            // Warm the immutable definition/dependency caches before measuring
            // the stable per-instance database query shape.
            _ = await PatchVariableAsync(target.Id, "approved", false);

            int baselineCommands;
            using (var evaluations = new ConditionalEvaluationProbe())
            {
                fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
                _ = await PatchVariableAsync(target.Id, "approved", false);
                baselineCommands = fixture.CommandCounter.ReaderCommands;
                Assert.Equal(1, evaluations.Count);
                Assert.DoesNotContain(
                    fixture.CommandCounter.ReaderCommandTexts,
                    command => MentionsTable(command, "instance_history"));
            }

            var unrelatedIds = new long[unrelatedInstanceCount];
            for (var index = 0; index < unrelatedInstanceCount; index++)
            {
                unrelatedIds[index] = (await StartAsync(
                    workflowId,
                    approved: false)).Id;
            }

            await using (var seed = fixture.CreateDbContext())
            {
                var now = DateTimeOffset.UtcNow;
                seed.InstanceHistory.AddRange(
                    unrelatedIds.SelectMany((instanceId, instanceIndex) =>
                        Enumerable.Range(0, historyRowsPerInstance).Select(row =>
                            new InstanceHistoryEntity
                            {
                                InstanceId = instanceId,
                                WorkflowDefinitionId = workflowId,
                                FromStepId = 1,
                                ToStepId = 2,
                                Note = "conditional-query-budget-seed",
                                PerformedAt = now.AddTicks(
                                    instanceIndex * historyRowsPerInstance + row)
                            })));
                await seed.SaveChangesAsync();
            }

            using (var evaluations = new ConditionalEvaluationProbe())
            {
                fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
                _ = await PatchVariableAsync(target.Id, "approved", false);

                Assert.Equal(1, evaluations.Count);
                Assert.Equal(
                    baselineCommands,
                    fixture.CommandCounter.ReaderCommands);
                Assert.DoesNotContain(
                    fixture.CommandCounter.ReaderCommandTexts,
                    command => MentionsTable(command, "instance_history"));
            }
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Observable_write_evaluates_once_for_many_tokens_waiting_on_one_node()
    {
        const int activationCount = 12;
        var workflowKey = $"conditional-evaluate-once-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                CreateSharedConditionalFanInWorkflow(
                    workflowKey,
                    activationCount));
            var started = await StartAsync(workflowId, approved: false);

            await using (var waiting = fixture.CreateDbContext())
            {
                Assert.Equal(
                    activationCount,
                    await waiting.ExecutionTokens.CountAsync(token =>
                        token.InstanceId == started.Id
                        && token.NodeId == 3
                        && token.Status == ExecutionTokenStatuses.Active));
            }

            using var evaluations = new ConditionalEvaluationProbe();
            _ = await PatchVariableAsync(started.Id, "approved", true);

            Assert.Equal(1, evaluations.Count);
            await using var completed = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await completed.WorkflowInstances.SingleAsync(instance =>
                    instance.Id == started.Id)).Status);
            Assert.Equal(
                activationCount,
                await completed.InstanceHistory.CountAsync(item =>
                    item.InstanceId == started.Id
                    && item.Note == InstanceHistoryNotes.ConditionalTriggered));
            Assert.Equal(
                activationCount,
                await completed.NodeExecutions.CountAsync(execution =>
                    execution.InstanceId == started.Id
                    && execution.NodeId == 3
                    && execution.CompletionReason
                        == NodeExecutionCompletionReasons.ConditionalTriggered));
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Concurrent_true_updates_latch_once_and_stale_worker_delivery_cannot_replay()
    {
        var workflowKey = $"conditional-concurrent-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync);
            var started = await StartAsync(workflowId, approved: false);

            var first = PatchVariableResponseAsync(started.Id, "approved", true);
            var second = PatchVariableResponseAsync(started.Id, "approved", true);
            var responses = await Task.WhenAll(first, second);
            try
            {
                Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            long jobId;
            await using (var db = fixture.CreateDbContext())
            {
                var job = await db.WorkflowJobs.SingleAsync(item =>
                    item.InstanceId == started.Id
                    && item.Kind == WorkflowJobKinds.ConditionalWake);
                jobId = job.Id;
                Assert.Single(await db.InstanceHistory
                    .Where(item => item.InstanceId == started.Id
                        && item.Note == InstanceHistoryNotes.ConditionalLatched)
                    .ToListAsync());
            }

            var lease = await LeaseJobAsync(jobId);
            await ProcessLeaseAsync(lease);
            // Simulate a duplicate delivery from the old worker generation.
            await ProcessLeaseAsync(lease);

            await using var after = fixture.CreateDbContext();
            Assert.Single(await after.InstanceHistory
                .Where(item => item.InstanceId == started.Id
                    && item.Note == InstanceHistoryNotes.ConditionalTriggered)
                .ToListAsync());
            Assert.Single(await after.NodeExecutions
                .Where(item => item.InstanceId == started.Id
                    && item.NodeId == 2
                    && item.CompletionReason
                        == NodeExecutionCompletionReasons.ConditionalTriggered)
                .ToListAsync());
            Assert.Single(await after.ExecutionTokens
                .Where(item => item.InstanceId == started.Id
                    && item.NodeId == 3
                    && item.Status == ExecutionTokenStatuses.Completed)
                .ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task User_task_output_that_normally_ends_its_branch_wakes_conditional_sibling()
    {
        var workflowKey = $"conditional-user-end-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                CreateParallelUserTaskWriterWorkflow(workflowKey));
            var started = await StartAsync(workflowId, approved: false);

            long taskId;
            await using (var before = fixture.CreateDbContext())
            {
                taskId = (await before.UserTasks.SingleAsync(task =>
                    task.InstanceId == started.Id
                    && task.Status == UserTaskStatuses.Active)).Id;
            }

            using var response = await SendAsync(
                HttpMethod.Post,
                $"/api/user-tasks/{taskId}/flows/40",
                new TakeFlowRequest(new Dictionary<string, JsonElement>
                {
                    ["approved"] = JsonSerializer.SerializeToElement(true)
                }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var after = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await after.WorkflowInstances.SingleAsync(instance =>
                    instance.Id == started.Id)).Status);
            Assert.Equal(2, await after.ExecutionTokens.CountAsync(token =>
                token.InstanceId == started.Id
                && token.Status == ExecutionTokenStatuses.Completed));
            Assert.Single(await after.InstanceHistory.Where(item =>
                item.InstanceId == started.Id
                && item.Note == InstanceHistoryNotes.ConditionalTriggered)
                .ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Message_output_that_normally_ends_its_branch_wakes_conditional_sibling()
    {
        var workflowKey = $"conditional-message-end-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                CreateParallelMessageWriterWorkflow(workflowKey));
            var started = await StartAsync(workflowId, approved: false);

            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/instances/{started.Id}/message");
            request.Headers.Add("X-Client-Id", "tests-client");
            request.Headers.Add("X-Client-Secret", "tests-secret");
            request.Headers.Add("X-Correlation", "accepted");
            request.Content = JsonContent.Create(
                new { approved = true },
                options: JsonOptions);
            using var response = await fixture.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var after = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await after.WorkflowInstances.SingleAsync(instance =>
                    instance.Id == started.Id)).Status);
            Assert.Single(await after.InstanceHistory.Where(item =>
                item.InstanceId == started.Id
                && item.Note == InstanceHistoryNotes.ConditionalTriggered)
                .ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Multi_instance_result_written_before_async_after_wakes_conditional_sibling()
    {
        var workflowKey = $"conditional-mi-async-after-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                CreateParallelMultiInstanceAsyncAfterWorkflow(workflowKey));
            var started = await StartAsync(workflowId, approved: false);

            long taskId;
            await using (var before = fixture.CreateDbContext())
            {
                taskId = (await before.UserTasks.SingleAsync(task =>
                    task.InstanceId == started.Id
                    && task.Status == UserTaskStatuses.Active)).Id;
            }

            using var response = await SendAsync(
                HttpMethod.Post,
                $"/api/user-tasks/{taskId}/flows/40",
                new TakeFlowRequest(null));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var after = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Running,
                (await after.WorkflowInstances.SingleAsync(instance =>
                    instance.Id == started.Id)).Status);
            var waiting = await after.ExecutionTokens.SingleAsync(token =>
                token.InstanceId == started.Id
                && token.Status == ExecutionTokenStatuses.Active);
            Assert.Equal(3, waiting.NodeId);
            Assert.Equal(ExecutionTokenWaitStates.AsyncAfter, waiting.WaitState);
            Assert.NotNull(waiting.WaitingJobId);
            Assert.Single(await after.InstanceHistory.Where(item =>
                item.InstanceId == started.Id
                && item.Note == InstanceHistoryNotes.ConditionalTriggered)
                .ToListAsync());
            Assert.Single(await after.ExecutionTokens.Where(token =>
                token.InstanceId == started.Id
                && token.NodeId == 6
                && token.Status == ExecutionTokenStatuses.Completed)
                .ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    private async Task<long> CreateWorkflowAsync(
        string workflowKey,
        string deliveryMode,
        bool approvedDefault = false)
        => await CreateWorkflowAsync(
            CreateWorkflow(workflowKey, deliveryMode, approvedDefault));

    private async Task<long> CreateWorkflowAsync(WorkflowModel definition)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(
                definition,
                true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId, bool approved)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(
                workflowId,
                null,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["approved"] = JsonSerializer.SerializeToElement(approved),
                    ["noise"] = JsonSerializer.SerializeToElement(false)
                }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<UpdateInstanceVariablesResultDto> PatchVariableAsync(
        long instanceId,
        string name,
        bool value)
    {
        using var response = await PatchVariableResponseAsync(instanceId, name, value);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UpdateInstanceVariablesResultDto>(response);
    }

    private Task<HttpResponseMessage> PatchVariableResponseAsync(
        long instanceId,
        string name,
        bool value) =>
        SendAsync(
            HttpMethod.Patch,
            $"/api/instances/{instanceId}/variables",
            new UpdateInstanceVariablesRequest(
                [new InstanceVariableWriteDto(
                    name,
                    JsonSerializer.SerializeToElement(value))],
                "conditional runtime test",
                $"conditional-update-{Guid.NewGuid():N}"),
            user: "operator");

    private async Task AssertWaitingWithoutJobAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        var instance = await db.WorkflowInstances.SingleAsync(item => item.Id == instanceId);
        var token = await db.ExecutionTokens.SingleAsync(item =>
            item.InstanceId == instanceId
            && item.Status == ExecutionTokenStatuses.Active);
        Assert.Equal(WorkflowInstanceStatuses.Running, instance.Status);
        Assert.Equal(2, token.NodeId);
        Assert.Null(token.WaitState);
        Assert.Null(token.WaitingJobId);
        Assert.False(await db.WorkflowJobs.AnyAsync(item =>
            item.InstanceId == instanceId
            && item.Kind == WorkflowJobKinds.ConditionalWake));
    }

    private async Task<WorkflowJobLeaseRecord> LeaseJobAsync(long jobId)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            Assert.Equal(
                1,
                await promote.WorkflowJobs
                    .Where(job => job.Id == jobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.Priority, int.MaxValue)
                        .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow)));
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"conditional-runtime:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 0,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        Assert.Equal(WorkflowJobClasses.Control, lease.Job.QueueClass);
        return lease;
    }

    private async Task ProcessLeaseAsync(WorkflowJobLeaseRecord lease)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
        await processor.ProcessAsync(lease, CancellationToken.None);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, ["admin"]);
        return fixture.Client.SendAsync(request);
    }

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowInstances
            .Where(instance => instance.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions
            .Where(definition => definition.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
    }

    private static WorkflowModel CreateParallelUserTaskWriterWorkflow(
        string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables = BooleanVariables(),
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Approve", Type = BpmnFlowNodeTypes.UserTask },
                ConditionalNode(4, "approved == true"),
                new FlowNodeModel { Id = 5, Name = "Writer end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Conditional end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 20, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 30, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel
                {
                    Id = 40,
                    SourceRef = 3,
                    TargetRef = 5,
                    Variables =
                    [
                        new VariableModel
                        {
                            Id = 100,
                            Name = "approved",
                            DataType = WorkflowVariableTypes.Boolean,
                            Required = true
                        }
                    ]
                },
                new SequenceFlowModel { Id = 50, SourceRef = 4, TargetRef = 6 }
            ]
        };

    private static WorkflowModel CreateParallelMessageWriterWorkflow(
        string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables = BooleanVariables(),
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Wait for message",
                    Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "tests-client",
                        ClientSecret = "tests-secret",
                        HeaderName = "X-Correlation",
                        HeaderValue = "accepted",
                        OutputMappings =
                        [
                            new MessageOutputMappingModel
                            {
                                Variable = "approved",
                                Path = "approved",
                                DataType = WorkflowVariableTypes.Boolean,
                                Required = true
                            }
                        ]
                    }
                },
                ConditionalNode(4, "approved == true"),
                new FlowNodeModel { Id = 5, Name = "Message end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Conditional end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 20, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 30, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 40, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 50, SourceRef = 4, TargetRef = 6 }
            ]
        };

    private static WorkflowModel CreateParallelMultiInstanceAsyncAfterWorkflow(
        string workflowKey) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables =
            [
                .. BooleanVariables(),
                new VariableModel
                {
                    Id = 3,
                    Name = "results",
                    DataType = WorkflowVariableTypes.Json,
                    DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Reviewers",
                    Type = BpmnFlowNodeTypes.UserTask,
                    AsyncAfter = true,
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "1",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                        ResultVariable = "results"
                    }
                },
                ConditionalNode(4, "Length(results) > 0"),
                new FlowNodeModel { Id = 5, Name = "Review end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Conditional end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 20, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 30, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel
                {
                    Id = 40,
                    Name = "Approve",
                    SourceRef = 3,
                    TargetRef = 5,
                    CompletionCondition = "CountFlow(40) >= 1",
                    CompletionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 41,
                    Name = "Default",
                    SourceRef = 3,
                    TargetRef = 5,
                    IsDefault = true,
                    IsSelectable = false
                },
                new SequenceFlowModel { Id = 50, SourceRef = 4, TargetRef = 6 }
            ]
        };

    private static List<VariableModel> BooleanVariables() =>
    [
        new VariableModel
        {
            Id = 1,
            Name = "approved",
            DataType = WorkflowVariableTypes.Boolean,
            Required = true,
            DefaultValue = JsonSerializer.SerializeToElement(false)
        },
        new VariableModel
        {
            Id = 2,
            Name = "noise",
            DataType = WorkflowVariableTypes.Boolean,
            Required = true,
            DefaultValue = JsonSerializer.SerializeToElement(false)
        }
    ];

    private static FlowNodeModel ConditionalNode(int id, string condition) =>
        new()
        {
            Id = id,
            Name = "Wait for condition",
            Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
            Conditional = new ConditionalDefinitionModel
            {
                Condition = condition,
                DeliveryMode = ConditionalEventDeliveryModes.Atomic
            }
        };

    private static WorkflowModel CreateWorkflow(
        string workflowKey,
        string deliveryMode,
        bool approvedDefault) =>
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
                    Name = "approved",
                    DataType = WorkflowVariableTypes.Boolean,
                    Required = true,
                    DefaultValue = JsonSerializer.SerializeToElement(approvedDefault)
                },
                new VariableModel
                {
                    Id = 2,
                    Name = "noise",
                    DataType = WorkflowVariableTypes.Boolean,
                    Required = true,
                    DefaultValue = JsonSerializer.SerializeToElement(false)
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
                    Name = "Wait for approval",
                    Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                    Conditional = new ConditionalDefinitionModel
                    {
                        Condition = "approved == true",
                        DeliveryMode = deliveryMode
                    }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Done",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel
                {
                    Id = 10,
                    Name = "Wait",
                    SourceRef = 1,
                    TargetRef = 2
                },
                new SequenceFlowModel
                {
                    Id = 20,
                    Name = "Continue",
                    SourceRef = 2,
                    TargetRef = 3
                }
            ]
        };

    private static WorkflowModel CreateSharedConditionalFanInWorkflow(
        string workflowKey,
        int activationCount)
    {
        var flows = new List<SequenceFlowModel>
        {
            new()
            {
                Id = 10,
                Name = "Fork",
                SourceRef = 1,
                TargetRef = 2
            }
        };
        flows.AddRange(Enumerable.Range(0, activationCount).Select(index =>
            new SequenceFlowModel
            {
                Id = 100 + index,
                Name = $"Activation {index + 1}",
                SourceRef = 2,
                TargetRef = 3
            }));
        flows.Add(new SequenceFlowModel
        {
            Id = 500,
            Name = "Continue",
            SourceRef = 3,
            TargetRef = 4
        });

        return new WorkflowModel
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables = BooleanVariables(),
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
                ConditionalNode(3, "approved == true"),
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Done",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows = flows
        };
    }

    private static bool MentionsTable(string commandText, string tableName) =>
        commandText.Contains(tableName, StringComparison.OrdinalIgnoreCase);

    private sealed class ConditionalEvaluationProbe : IDisposable
    {
        private const string MeterName = "Flowbit.Runtime.ConditionalEvents";
        private const string InstrumentName = "flowbit.conditional.evaluations";
        private readonly MeterListener listener = new();
        private long count;

        public ConditionalEvaluationProbe()
        {
            listener.InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == MeterName
                    && instrument.Name == InstrumentName)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
                Interlocked.Add(ref count, value));
            listener.Start();
        }

        public long Count => Interlocked.Read(ref count);

        public void Dispose() => listener.Dispose();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException(
            $"Response did not contain {typeof(T).Name}.");
}
