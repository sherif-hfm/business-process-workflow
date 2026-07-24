using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class ParallelGatewayAdvancedApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ActorRoles = ["User"];

    [Fact]
    public async Task InnerScopeInterrupt_PreservesOuterSiblingAndOuterController()
    {
        var workflowId = await CreateWorkflowAsync(CreateNestedParallelWorkflow());
        var started = await StartAsync(workflowId);
        var innerController = Assert.Single(
            (await ListTasksAsync(started.Id, "active")).Items,
            task => task.NodeId == 7);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{innerController.Id}/flows/701",
            new TakeFlowRequest(null),
            "inner-manager");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal("running", ack.InstanceStatus);
        Assert.Equal(
            new[] { 3, 4, 10 },
            ack.ExecutionPositions
                .Where(position => position.TokenStatus == ExecutionTokenStatuses.Active)
                .Select(position => position.NodeId)
                .Order()
                .ToArray());
        var cancelled = Assert.Single(
            ack.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Cancelled);
        Assert.Equal(8, cancelled.NodeId);
        Assert.Equal("parallelScopeCancelled", cancelled.TerminationReason);

        var detail = await GetInstanceAsync(started.Id);
        var outer = Assert.Single(
            detail.ParallelGatewayExecutions,
            execution => execution.ForkNodeId == 2);
        var inner = Assert.Single(
            detail.ParallelGatewayExecutions,
            execution => execution.ForkNodeId == 6);
        Assert.Equal(ParallelGatewayExecutionStatuses.Active, outer.Status);
        Assert.Equal(ParallelGatewayExecutionStatuses.Interrupted, inner.Status);
        Assert.Single(detail.History, entry =>
            entry.Note == "parallelInterrupt" && entry.FromNodeId == 9);
    }

    [Fact]
    public async Task OuterScopeInterrupt_CancelsNestedExecutionAndAllDescendantWork()
    {
        var workflowId = await CreateWorkflowAsync(CreateNestedParallelWorkflow());
        var started = await StartAsync(workflowId);
        var outerController = Assert.Single(
            (await ListTasksAsync(started.Id, "active")).Items,
            task => task.NodeId == 3);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{outerController.Id}/flows/301",
            new TakeFlowRequest(null),
            "outer-manager");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal("running", ack.InstanceStatus);
        Assert.Equal(
            [12],
            ack.ExecutionPositions
                .Where(position => position.TokenStatus == ExecutionTokenStatuses.Active)
                .Select(position => position.NodeId)
                .ToArray());
        Assert.Equal(3, ack.ExecutionPositions.Count(position =>
            position.TokenStatus == ExecutionTokenStatuses.Cancelled));
        Assert.All(
            ack.ExecutionPositions.Where(position =>
                position.TokenStatus == ExecutionTokenStatuses.Cancelled),
            position => Assert.Equal("parallelScopeCancelled", position.TerminationReason));

        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal(
            ParallelGatewayExecutionStatuses.Interrupted,
            Assert.Single(
                detail.ParallelGatewayExecutions,
                execution => execution.ForkNodeId == 2).Status);
        Assert.Equal(
            ParallelGatewayExecutionStatuses.Cancelled,
            Assert.Single(
                detail.ParallelGatewayExecutions,
                execution => execution.ForkNodeId == 6).Status);
        Assert.Single((await ListTasksAsync(started.Id, "active")).Items);
    }

    [Fact]
    public async Task ScopedInterrupt_CancelsMessageWaitAndMultiInstanceBranch()
    {
        var workflowId = await CreateWorkflowAsync(CreateMessageAndMultiInstanceWorkflow());
        var started = await StartAsync(workflowId);
        var activeTasks = (await ListTasksAsync(started.Id, "active")).Items;
        var manager = Assert.Single(activeTasks, task => task.NodeId == 3);
        Assert.Equal(2, activeTasks.Count(task => task.NodeId == 5));
        Assert.Contains(
            started.ExecutionPositions,
            position => position.NodeId == 4
                        && position.NodeType == BpmnFlowNodeTypes.IntermediateMessageCatchEvent);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{manager.Id}/flows/301",
            new TakeFlowRequest(null),
            "manager");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal(
            [8],
            ack.ExecutionPositions
                .Where(position => position.TokenStatus == ExecutionTokenStatuses.Active)
                .Select(position => position.NodeId)
                .ToArray());
        Assert.Contains(
            ack.ExecutionPositions,
            position => position.NodeId == 4
                        && position.TokenStatus == ExecutionTokenStatuses.Cancelled);
        Assert.Contains(
            ack.ExecutionPositions,
            position => position.NodeId == 5
                        && position.TokenStatus == ExecutionTokenStatuses.Cancelled);

        var detail = await GetInstanceAsync(started.Id);
        var multi = Assert.Single(detail.MultiInstances);
        Assert.Equal("cancelled", multi.Status);
        await using var db = fixture.CreateDbContext();
        var execution = await db.MultiInstanceExecutions.SingleAsync(row =>
            row.InstanceId == started.Id);
        Assert.Equal(MultiInstanceExecutionStatuses.Cancelled, execution.Status);
        var childTasks = await db.UserTasks
            .Where(task => task.MultiInstanceExecutionId == execution.Id)
            .ToListAsync();
        Assert.Equal(2, childTasks.Count);
        Assert.All(childTasks, task => Assert.Equal(UserTaskStatuses.Cancelled, task.Status));
    }

    [Fact]
    public async Task ParallelMessageCatches_RequireExactSelectorAndDeliverOnlySelectedToken()
    {
        var workflowId = await CreateWorkflowAsync(CreateParallelMessageCatchWorkflow());
        var started = await StartAsync(workflowId);

        using (var ambiguous = await SendMessageAsync(started.Id))
        {
            Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        }
        using (var wrongCase = await SendMessageAsync(started.Id, "finance-wait"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongCase.StatusCode);
        }

        using (var selected = await SendMessageAsync(started.Id, "Finance-Wait"))
        {
            Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        }

        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal("running", detail.Status);
        Assert.Equal(
            new[] { 4, 5 },
            detail.ExecutionPositions
                .Where(position => position.TokenStatus == ExecutionTokenStatuses.Active)
                .Select(position => position.NodeId)
                .Order()
                .ToArray());
        Assert.DoesNotContain(
            detail.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Active
                        && position.NodeId is 3 or 6);
        Assert.Contains(
            detail.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Active
                        && position.NodeId == 4
                        && position.NodeExternalId == "Legal-Wait");
        Assert.Contains(
            detail.History,
            entry => entry.Note == "message" && entry.FromNodeId == 3);
        Assert.Equal(
            5,
            Assert.Single((await ListTasksAsync(started.Id, "active")).Items).NodeId);
    }

    [Fact]
    public async Task LowerIdDirectInterrupt_PreemptsHigherIdTerminateBranch()
    {
        var workflowId = await CreateWorkflowAsync(CreateForkSchedulerWorkflow(
            directInterrupt: true));

        var started = await StartAsync(workflowId);

        Assert.Equal("running", started.Status);
        Assert.Null(started.Completion);
        Assert.Equal(
            5,
            Assert.Single(
                started.ExecutionPositions,
                position => position.TokenStatus == ExecutionTokenStatuses.Active).NodeId);
        var cancelled = Assert.Single(
            started.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Cancelled);
        Assert.Equal(2, cancelled.NodeId);
        Assert.Equal("parallelScopeCancelled", cancelled.TerminationReason);
        Assert.DoesNotContain(started.History, entry => entry.ToNodeId == 4);
        Assert.Equal(
            ParallelGatewayExecutionStatuses.Interrupted,
            Assert.Single(started.ParallelGatewayExecutions).Status);
        Assert.Equal(5, Assert.Single((await ListTasksAsync(started.Id, "active")).Items).NodeId);
    }

    [Fact]
    public async Task LowerIdAutomaticInterrupt_CancelsAlreadyQueuedServiceBranch()
    {
        var workflowId = await CreateWorkflowAsync(CreateForkSchedulerWorkflow(
            directInterrupt: false));

        var started = await StartAsync(workflowId);

        Assert.Equal("running", started.Status);
        Assert.Equal(
            6,
            Assert.Single(
                started.ExecutionPositions,
                position => position.TokenStatus == ExecutionTokenStatuses.Active).NodeId);
        var cancelled = Assert.Single(
            started.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Cancelled);
        Assert.Equal(4, cancelled.NodeId);
        Assert.Equal("parallelScopeCancelled", cancelled.TerminationReason);
        Assert.Contains(
            started.History,
            entry => entry.Note == "automatic" && entry.FromNodeId == 3 && entry.ToNodeId == 5);
        Assert.DoesNotContain(started.History, entry => entry.Note == "service");
        Assert.Equal(
            ParallelGatewayExecutionStatuses.Interrupted,
            Assert.Single(started.ParallelGatewayExecutions).Status);
        Assert.Equal(6, Assert.Single((await ListTasksAsync(started.Id, "active")).Items).NodeId);
    }

    [Theory]
    [InlineData("automatic")]
    [InlineData("service")]
    [InlineData("script")]
    [InlineData("boundary")]
    public async Task AutomaticServiceScriptOrBoundaryTrigger_InterruptsScopeWithoutUserAction(
        string triggerKind)
    {
        var workflowId = await CreateWorkflowAsync(
            CreateAutomaticInterruptWorkflow(triggerKind));

        var started = await StartAsync(workflowId);

        Assert.Equal("running", started.Status);
        Assert.Equal(
            [7],
            started.ExecutionPositions
                .Where(position => position.TokenStatus == ExecutionTokenStatuses.Active)
                .Select(position => position.NodeId)
                .ToArray());
        var cancelledSibling = Assert.Single(
            started.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Cancelled);
        Assert.Equal(4, cancelledSibling.NodeId);
        Assert.Equal("parallelScopeCancelled", cancelledSibling.TerminationReason);
        Assert.Contains(started.History, entry => entry.Note == "parallelInterrupt");
        if (triggerKind == "boundary")
        {
            Assert.Contains(started.History, entry => entry.Note == "error");
            Assert.Contains(started.History, entry => entry.Note == "boundary");
        }
        else
        {
            Assert.Contains(started.History, entry =>
                entry.Note == triggerKind && entry.FromNodeId == 3);
        }
        if (triggerKind == "script")
        {
            Assert.Equal(
                "scripted",
                started.Variables.Last(variable =>
                    variable.VariableName == "triggerMarker").Value.GetString());
        }

        Assert.Equal(7, Assert.Single((await ListTasksAsync(started.Id, "active")).Items).NodeId);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent, "completed", ExecutionTokenStatuses.Completed)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent, "faulted", ExecutionTokenStatuses.Faulted)]
    public async Task ScopedInterrupt_CanContinueDirectlyToOrdinaryOrErrorEnd(
        string terminalType,
        string expectedInstanceStatus,
        string expectedTokenStatus)
    {
        var workflowId = await CreateWorkflowAsync(CreateInterruptTerminalWorkflow(terminalType));
        var started = await StartAsync(workflowId);
        var manager = Assert.Single(
            (await ListTasksAsync(started.Id, "active")).Items,
            task => task.NodeId == 3);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{manager.Id}/flows/301",
            new TakeFlowRequest(null),
            "manager");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal(expectedInstanceStatus, ack.InstanceStatus);
        Assert.Contains(
            ack.ExecutionPositions,
            position => position.NodeId == 6 && position.TokenStatus == expectedTokenStatus);
        Assert.Contains(
            ack.ExecutionPositions,
            position => position.NodeId == 4
                        && position.TokenStatus == ExecutionTokenStatuses.Cancelled);
        if (terminalType == BpmnFlowNodeTypes.EndEvent)
        {
            Assert.Equal(
                WorkflowCompletionKinds.Normal,
                Assert.IsType<CompletionInfoDto>(ack.Completion).Kind);
            Assert.Null(ack.Fault);
        }
        else
        {
            Assert.Null(ack.Completion);
            Assert.Equal(
                "INTERRUPT_FAILURE",
                Assert.IsType<FaultInfoDto>(ack.Fault).Code);
        }
    }

    [Fact]
    public async Task CompetingInterruptAndTerminateAction_LeavesOneStaleConflict()
    {
        var workflowId = await CreateWorkflowAsync(CreateCompetingActionsWorkflow());
        var started = await StartAsync(workflowId);
        var tasks = (await ListTasksAsync(started.Id, "active")).Items;
        var manager = Assert.Single(tasks, task => task.NodeId == 3);
        var worker = Assert.Single(tasks, task => task.NodeId == 4);

        var responses = await Task.WhenAll(
            SendAsync(
                HttpMethod.Post,
                $"/api/user-tasks/{manager.Id}/flows/301",
                new TakeFlowRequest(null),
                "manager"),
            SendAsync(
                HttpMethod.Post,
                $"/api/user-tasks/{worker.Id}/flows/401",
                new TakeFlowRequest(null),
                "worker"));
        try
        {
            Assert.Single(responses, item => item.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        var detail = await GetInstanceAsync(started.Id);
        Assert.Contains(detail.Status, new[] { "running", "completed" });
        if (detail.Status == "running")
        {
            Assert.Equal(7, Assert.Single((await ListTasksAsync(started.Id, "active")).Items).NodeId);
            Assert.Single(detail.History, entry => entry.Note == "parallelInterrupt");
        }
        else
        {
            Assert.Empty((await ListTasksAsync(started.Id, "active")).Items);
            Assert.Equal(
                WorkflowCompletionKinds.Terminate,
                Assert.IsType<CompletionInfoDto>(detail.Completion).Kind);
        }
    }

    [Fact]
    public async Task DownstreamFailure_RollsBackInterruptAndSiblingCancellations()
    {
        var model = ParallelGatewayApiTests.CreateParallelWorkflow();
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 14,
            Name = "Fail after interrupt",
            Type = BpmnFlowNodeTypes.ServiceTask,
            Service = new ServiceTaskModel
            {
                Url = "https://tests.local/unconfigured-after-interrupt"
            }
        });
        model.SequenceFlows.Single(flow => flow.Id == 901).TargetRef = 14;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 1401,
            Name = "Continue",
            SourceRef = 14,
            TargetRef = 10
        });
        var workflowId = await CreateWorkflowAsync(model);
        var started = await StartAsync(workflowId);
        var manager = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{manager.Id}/flows/302",
            new TakeFlowRequest(null),
            "manager");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal("running", detail.Status);
        Assert.Equal(
            new[] { 3, 6, 6 },
            detail.ExecutionPositions
                .Where(position => position.TokenStatus == ExecutionTokenStatuses.Active)
                .Select(position => position.NodeId)
                .Order()
                .ToArray());
        Assert.DoesNotContain(
            detail.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Cancelled);
        Assert.Equal(
            ParallelGatewayExecutionStatuses.Active,
            Assert.Single(detail.ParallelGatewayExecutions).Status);
        Assert.DoesNotContain(detail.History, entry => entry.Note == "parallelInterrupt");
        Assert.Equal(3, Assert.Single((await ListTasksAsync(started.Id, "active")).Items).NodeId);
    }

    [Fact]
    public async Task ScopedCancellation_DoesNotFabricateSiblingTraversalEvidence()
    {
        var workflowId = await CreateWorkflowAsync(CreateFlowEvidenceInterruptWorkflow());
        var started = await StartAsync(workflowId);
        var manager = Assert.Single(
            (await ListTasksAsync(started.Id, "active")).Items,
            task => task.NodeId == 3);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{manager.Id}/flows/301",
            new TakeFlowRequest(null),
            "manager");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal(7, Assert.Single(
            ack.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Active).NodeId);
        var detail = await GetInstanceAsync(started.Id);
        var audit = detail.Variables.Last(variable => variable.VariableName == "cancelledFlowAudit");
        Assert.Equal(0, audit.Value.GetProperty("actions").GetProperty("count").GetInt64());
        Assert.Equal(0, audit.Value.GetProperty("traversals").GetProperty("count").GetInt64());

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.SequenceFlowOccurrences.AnyAsync(occurrence =>
            occurrence.InstanceId == started.Id && occurrence.SequenceFlowId == 401));
        Assert.True(await db.SequenceFlowOccurrences.AnyAsync(occurrence =>
            occurrence.InstanceId == started.Id && occurrence.SequenceFlowId == 301
                                                && occurrence.IsTraversal));
        Assert.True(await db.SequenceFlowOccurrences.AnyAsync(occurrence =>
            occurrence.InstanceId == started.Id && occurrence.SequenceFlowId == 501
                                                && occurrence.IsTraversal));
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<PagedResult<UserTaskDto>> ListTasksAsync(long instanceId, string status)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status={status}&page=1&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<UserTaskDto>>(response);
    }

    private async Task<HttpResponseMessage> SendMessageAsync(
        long instanceId,
        string? catchEvent = null)
    {
        var path = $"/api/instances/{instanceId}/message";
        if (catchEvent is not null)
        {
            path += "?catchEvent=" + Uri.EscapeDataString(catchEvent);
        }
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { }, options: JsonOptions)
        };
        request.Headers.Add("X-Client-Id", "tests-client");
        request.Headers.Add("X-Client-Secret", "tests-secret");
        request.Headers.Add("X-Correlation", "accepted");
        return await fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(
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
        ApiTestAuth.Authorize(request, user, ActorRoles);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateNestedParallelWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "nested-parallel-" + suffix,
            Name = "Nested parallel " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Outer fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Outer controller", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Outer sibling", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 5, Name = "Enter inner", Type = BpmnFlowNodeTypes.Task },
                new FlowNodeModel { Id = 6, Name = "Inner fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 7, Name = "Inner controller", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 8, Name = "Inner sibling", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 9,
                    Name = "Interrupt inner",
                    Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                    ParallelGatewayRef = 6
                },
                new FlowNodeModel { Id = 10, Name = "After inner interrupt", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 11,
                    Name = "Interrupt outer",
                    Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                    ParallelGatewayRef = 2
                },
                new FlowNodeModel { Id = 12, Name = "After outer interrupt", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 20, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 203, SourceRef = 2, TargetRef = 5 },
                new SequenceFlowModel { Id = 301, Name = "Interrupt outer", SourceRef = 3, TargetRef = 11 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 20 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 7 },
                new SequenceFlowModel { Id = 602, SourceRef = 6, TargetRef = 8 },
                new SequenceFlowModel { Id = 701, Name = "Interrupt inner", SourceRef = 7, TargetRef = 9 },
                new SequenceFlowModel { Id = 801, SourceRef = 8, TargetRef = 20 },
                new SequenceFlowModel { Id = 901, SourceRef = 9, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 20 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 12 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 20 }
            ]
        };
    }

    private static WorkflowModel CreateMessageAndMultiInstanceWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "parallel-message-mi-" + suffix,
            Name = "Parallel message and MI " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "miResults",
                    DataType = WorkflowVariableTypes.Json,
                    DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Manager", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Message wait",
                    ExternalId = "parallel-message-wait",
                    Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "tests-client",
                        ClientSecret = "tests-secret",
                        HeaderName = "X-Correlation",
                        HeaderValue = "accepted"
                    }
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Parallel reviewers",
                    Type = BpmnFlowNodeTypes.UserTask,
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "2",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                        ResultVariable = "miResults"
                    }
                },
                new FlowNodeModel { Id = 6, Name = "End", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 7,
                    Name = "Interrupt",
                    Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                    ParallelGatewayRef = 2
                },
                new FlowNodeModel { Id = 8, Name = "After interrupt", Type = BpmnFlowNodeTypes.UserTask }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 203, SourceRef = 2, TargetRef = 5 },
                new SequenceFlowModel { Id = 301, Name = "Override", SourceRef = 3, TargetRef = 7 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 6 },
                new SequenceFlowModel
                {
                    Id = 501,
                    Name = "Approve",
                    SourceRef = 5,
                    TargetRef = 6,
                    CompletionCondition = "CountFlow(501) >= 2",
                    CompletionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 502,
                    Name = "No outcome",
                    SourceRef = 5,
                    TargetRef = 6,
                    IsDefault = true,
                    IsSelectable = false
                },
                new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 8 },
                new SequenceFlowModel { Id = 801, SourceRef = 8, TargetRef = 6 }
            ]
        };
    }

    private static WorkflowModel CreateParallelMessageCatchWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        static FlowNodeModel MessageCatch(int id, string name, string externalId) =>
            new()
            {
                Id = id,
                Name = name,
                ExternalId = externalId,
                Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                Message = new MessageCatchModel
                {
                    ClientId = "tests-client",
                    ClientSecret = "tests-secret",
                    HeaderName = "X-Correlation",
                    HeaderValue = "accepted"
                }
            };

        return new WorkflowModel
        {
            Id = "parallel-message-selection-" + suffix,
            Name = "Parallel message selection " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                MessageCatch(3, "Finance wait", "Finance-Wait"),
                MessageCatch(4, "Legal wait", "Legal-Wait"),
                new FlowNodeModel { Id = 5, Name = "Finance received", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 6, Name = "Legal received", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 7, Name = "Finance end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 8, Name = "Legal end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 6 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 7 },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 8 }
            ]
        };
    }

    private static WorkflowModel CreateForkSchedulerWorkflow(bool directInterrupt)
    {
        var suffix = Guid.NewGuid().ToString("N");
        if (directInterrupt)
        {
            return new WorkflowModel
            {
                Id = "parallel-direct-interrupt-order-" + suffix,
                Name = "Parallel direct interrupt ordering " + suffix,
                InitialEventId = 1,
                FlowNodes =
                [
                    new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                    new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                    new FlowNodeModel
                    {
                        Id = 3,
                        Name = "Interrupt first",
                        Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                        ParallelGatewayRef = 2
                    },
                    new FlowNodeModel
                    {
                        Id = 4,
                        Name = "Terminate later",
                        Type = BpmnFlowNodeTypes.TerminateEndEvent
                    },
                    new FlowNodeModel { Id = 5, Name = "Interrupt won", Type = BpmnFlowNodeTypes.UserTask },
                    new FlowNodeModel { Id = 6, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
                ],
                SequenceFlows =
                [
                    new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                    new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                    new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                    new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                    new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 }
                ]
            };
        }

        return new WorkflowModel
        {
            Id = "parallel-queued-service-order-" + suffix,
            Name = "Parallel queued service ordering " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Automatic trigger", Type = BpmnFlowNodeTypes.Task },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Queued service",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    Service = new ServiceTaskModel
                    {
                        Url = "https://tests.local/unconfigured-queued-parallel-service"
                    }
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Interrupt",
                    Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                    ParallelGatewayRef = 2
                },
                new FlowNodeModel { Id = 6, Name = "Interrupt won", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 7, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 7 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 7 }
            ]
        };
    }

    private static WorkflowModel CreateAutomaticInterruptWorkflow(string triggerKind)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var useBoundaryFailure = triggerKind == "boundary";
        var trigger = new FlowNodeModel
        {
            Id = 3,
            Name = triggerKind + " trigger",
            Type = triggerKind switch
            {
                "service" or "boundary" => BpmnFlowNodeTypes.ServiceTask,
                "script" => BpmnFlowNodeTypes.ScriptTask,
                _ => BpmnFlowNodeTypes.Task
            },
            Service = triggerKind switch
            {
                "service" => new ServiceTaskModel
                {
                    Url = "https://tests.local/typed-output-success"
                },
                "boundary" => new ServiceTaskModel
                {
                    Url = "https://tests.local/unconfigured-boundary-trigger"
                },
                _ => null
            },
            ScriptFormat = ScriptFormats.NCalc,
            Assignments = triggerKind == "script"
                ?
                [
                    new AssignmentModel
                    {
                        Variable = "triggerMarker",
                        Expression = "'scripted'"
                    }
                ]
                : []
        };
        var nodes = new List<FlowNodeModel>
        {
            new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new() { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
            trigger,
            new() { Id = 4, Name = "Sibling", Type = BpmnFlowNodeTypes.UserTask },
            new()
            {
                Id = 6,
                Name = "Interrupt",
                Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                ParallelGatewayRef = 2
            },
            new() { Id = 7, Name = "After interrupt", Type = BpmnFlowNodeTypes.UserTask },
            new() { Id = 8, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        };
        var flows = new List<SequenceFlowModel>
        {
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, SourceRef = 2, TargetRef = 3 },
            new() { Id = 202, SourceRef = 2, TargetRef = 4 },
            new() { Id = 401, SourceRef = 4, TargetRef = 8 },
            new() { Id = 601, SourceRef = 6, TargetRef = 7 },
            new() { Id = 701, SourceRef = 7, TargetRef = 8 }
        };
        if (useBoundaryFailure)
        {
            nodes.Add(new FlowNodeModel
            {
                Id = 5,
                Name = "Catch failure",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 3
            });
            flows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 8 });
            flows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 });
        }
        else
        {
            flows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 6 });
        }

        return new WorkflowModel
        {
            Id = "automatic-interrupt-" + suffix,
            Name = "Automatic interrupt " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "triggerMarker",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement("initial")
                }
            ],
            FlowNodes = nodes,
            SequenceFlows = flows
        };
    }

    private static WorkflowModel CreateInterruptTerminalWorkflow(string terminalType)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "interrupt-terminal-" + suffix,
            Name = "Interrupt terminal " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Manager", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Sibling", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Interrupt",
                    Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                    ParallelGatewayRef = 2
                },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Interrupt terminal",
                    Type = terminalType,
                    ErrorCode = terminalType == BpmnFlowNodeTypes.ErrorEndEvent
                        ? "INTERRUPT_FAILURE"
                        : null
                },
                new FlowNodeModel { Id = 7, Name = "Sibling end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 7 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 }
            ]
        };
    }

    private static WorkflowModel CreateCompetingActionsWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "competing-interrupt-" + suffix,
            Name = "Competing interrupt " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Manager", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Worker", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Interrupt",
                    Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                    ParallelGatewayRef = 2
                },
                new FlowNodeModel { Id = 6, Name = "Terminate", Type = BpmnFlowNodeTypes.TerminateEndEvent },
                new FlowNodeModel { Id = 7, Name = "After interrupt", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 8, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 6 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 7 },
                new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 8 }
            ]
        };
    }

    private static WorkflowModel CreateFlowEvidenceInterruptWorkflow()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "parallel-flow-evidence-" + suffix,
            Name = "Parallel flow evidence " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "cancelledFlowAudit",
                    DataType = WorkflowVariableTypes.Json,
                    DefaultValue = JsonSerializer.SerializeToElement(new { })
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Manager", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Sibling", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Interrupt",
                    Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                    ParallelGatewayRef = 2
                },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Observe cancelled flow",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.NCalc,
                    Assignments =
                    [
                        new AssignmentModel
                        {
                            Variable = "cancelledFlowAudit",
                            Expression = "FlowInfo(401, 'all')"
                        }
                    ]
                },
                new FlowNodeModel { Id = 7, Name = "After interrupt", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 8, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 8 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 7 },
                new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 8 }
            ]
        };
    }
}
