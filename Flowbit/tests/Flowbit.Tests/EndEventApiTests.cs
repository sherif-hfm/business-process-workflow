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
public sealed class EndEventApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ActorRoles = ["User"];

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent, "completed", ExecutionTokenStatuses.Completed)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent, "faulted", ExecutionTokenStatuses.Faulted)]
    public async Task UserTaskFlow_EnteringTerminalEventFinalizesAllRuntimeProjections(
        string terminalType,
        string expectedInstanceStatus,
        string expectedTokenStatus)
    {
        var workflowId = await CreateWorkflowAsync(CreateUserTaskTerminalModel(terminalType));
        var started = await StartAtUserTaskAsync(workflowId);
        var task = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);

        using var completed = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "finisher");

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(completed);
        Assert.Equal(UserTaskStatuses.Completed, ack.TaskStatus);
        Assert.Equal(expectedInstanceStatus, ack.InstanceStatus);
        Assert.Equal(3, ack.CurrentNodeId);
        Assert.Equal("Terminal", ack.CurrentNodeName);
        Assert.Equal("terminal-event", ack.CurrentNodeExternalId);
        AssertFault(
            terminalType,
            ack.Fault,
            "USER_TASK_FAULT",
            "The user task ended in a fault.");

        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal(expectedInstanceStatus, detail.Status);
        Assert.Equal(3, detail.CurrentNodeId);
        Assert.Equal("Terminal", detail.CurrentNodeName);
        Assert.Equal("terminal-event", detail.CurrentNodeExternalId);
        Assert.Null(detail.MultiInstance);
        Assert.Null(detail.UserTasks);
        AssertFault(
            terminalType,
            detail.Fault,
            "USER_TASK_FAULT",
            "The user task ended in a fault.");

        var finalHistory = Assert.Single(detail.History, entry => entry.SequenceFlowId == 201);
        Assert.Equal(2, finalHistory.FromNodeId);
        Assert.Equal(3, finalHistory.ToNodeId);
        Assert.Equal("finisher", finalHistory.PerformedBy);

        using (var taskFlows = await SendAsync(HttpMethod.Get, $"/api/user-tasks/{task.Id}/flows", user: "finisher"))
        {
            Assert.Equal(HttpStatusCode.OK, taskFlows.StatusCode);
            Assert.Empty(await ReadAsync<List<SequenceFlowModel>>(taskFlows));
        }

        using (var instanceFlows = await SendAsync(HttpMethod.Get, $"/api/instances/{started.Id}/flows", user: "finisher"))
        {
            Assert.Equal(HttpStatusCode.OK, instanceFlows.StatusCode);
            Assert.Empty(await ReadAsync<List<SequenceFlowModel>>(instanceFlows));
        }

        Assert.Empty((await ListTasksAsync(started.Id, "active", "finisher")).Items);
        using (var inbox = await SendAsync(
                   HttpMethod.Get,
                   $"/api/instances/inbox?instanceId={started.Id}&pageSize=20",
                   user: "finisher"))
        {
            Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
            Assert.Empty((await ReadAsync<PagedResult<InboxItemDto>>(inbox)).Items);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var instance = await db.WorkflowInstances.SingleAsync(row => row.Id == started.Id);
            Assert.Equal(expectedInstanceStatus, instance.Status);

            var token = await db.ExecutionTokens.SingleAsync(row => row.InstanceId == started.Id);
            Assert.Equal(3, token.NodeId);
            Assert.Equal(terminalType, token.NodeType);
            Assert.Equal(expectedTokenStatus, token.Status);
            if (terminalType == BpmnFlowNodeTypes.ErrorEndEvent)
            {
                Assert.Equal("USER_TASK_FAULT", token.FaultCode);
                Assert.Equal("The user task ended in a fault.", token.FaultDescription);
            }
            else
            {
                Assert.Null(token.FaultCode);
                Assert.Null(token.FaultDescription);
            }

            var storedTask = await db.UserTasks.SingleAsync(row => row.Id == task.Id);
            Assert.Equal(UserTaskStatuses.Completed, storedTask.Status);
            Assert.NotNull(storedTask.CompletedAt);
            Assert.False(await db.UserTasks.AnyAsync(row =>
                row.InstanceId == started.Id
                && (row.Status == UserTaskStatuses.Active || row.Status == UserTaskStatuses.Pending)));

            var history = await db.InstanceHistory.SingleAsync(row =>
                row.InstanceId == started.Id && row.ActionId == 201);
            Assert.Equal(2, history.FromStepId);
            Assert.Equal(3, history.ToStepId);
            Assert.Equal("finisher", history.PerformedBy);
        }

        using (var duplicate = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/flows/201",
                   new TakeFlowRequest(null),
                   "finisher"))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var legacyDuplicate = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{started.Id}/flows/201",
                   new TakeFlowRequest(null),
                   "finisher"))
        {
            Assert.Equal(HttpStatusCode.Conflict, legacyDuplicate.StatusCode);
        }

        using var cancel = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{started.Id}/cancel",
            user: "finisher");
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);

        using var filteredResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/instances?status=faulted&instanceId={started.Id}&page=1&pageSize=20",
            user: "finisher");
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);
        var filtered = await ReadAsync<PagedResult<InstanceSummaryDto>>(filteredResponse);
        if (terminalType == BpmnFlowNodeTypes.ErrorEndEvent)
        {
            var summary = Assert.Single(filtered.Items);
            Assert.Equal(3, summary.CurrentNodeId);
            Assert.NotNull(summary.Fault);
            Assert.Equal("USER_TASK_FAULT", summary.Fault.Code);
        }
        else
        {
            Assert.Empty(filtered.Items);
        }
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent, "completed", ExecutionTokenStatuses.Completed)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent, "faulted", ExecutionTokenStatuses.Faulted)]
    public async Task ConcurrentFinalActions_AdvanceExactlyOnceAndReturnConflictToTheLoser(
        string terminalType,
        string expectedInstanceStatus,
        string expectedTokenStatus)
    {
        var workflowId = await CreateWorkflowAsync(CreateUserTaskTerminalModel(terminalType));
        var started = await StartAtUserTaskAsync(workflowId);
        var task = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);

        var requests = new[]
        {
            SendAsync(HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/201", new TakeFlowRequest(null), "finisher"),
            SendAsync(HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/201", new TakeFlowRequest(null), "finisher")
        };
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal(expectedInstanceStatus, detail.Status);
        Assert.Equal(3, detail.CurrentNodeId);
        AssertFault(terminalType, detail.Fault, "USER_TASK_FAULT", "The user task ended in a fault.");
        Assert.Single(detail.History, entry => entry.SequenceFlowId == 201);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.InstanceHistory.CountAsync(row =>
            row.InstanceId == started.Id && row.ActionId == 201));
        var storedTask = await db.UserTasks.SingleAsync(row => row.Id == task.Id);
        Assert.Equal(UserTaskStatuses.Completed, storedTask.Status);
        var token = await db.ExecutionTokens.SingleAsync(row => row.InstanceId == started.Id);
        Assert.Equal(expectedTokenStatus, token.Status);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent, "completed")]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent, "faulted")]
    public async Task ConcurrentFinalActionAndCancellation_HaveOneAtomicWinner(
        string terminalType,
        string expectedTerminalStatus)
    {
        var workflowId = await CreateWorkflowAsync(CreateUserTaskTerminalModel(terminalType));
        var started = await StartAtUserTaskAsync(workflowId);
        var task = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);

        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/201", new TakeFlowRequest(null), "finisher"),
            SendAsync(HttpMethod.Post, $"/api/instances/{started.Id}/cancel", user: "finisher"));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Single(responses, response =>
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        var detail = await GetInstanceAsync(started.Id);
        Assert.Contains(detail.Status, new[] { expectedTerminalStatus, "cancelled" });
        var terminalWon = detail.Status == expectedTerminalStatus;
        Assert.Equal(terminalWon ? 3 : 2, detail.CurrentNodeId);
        Assert.Equal(terminalWon ? 1 : 0,
            detail.History.Count(entry => entry.SequenceFlowId == 201));
        if (terminalWon)
            AssertFault(terminalType, detail.Fault, "USER_TASK_FAULT", "The user task ended in a fault.");
        else
            Assert.Null(detail.Fault);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(terminalWon ? 1 : 0,
            await db.InstanceHistory.CountAsync(row => row.InstanceId == started.Id && row.ActionId == 201));
        var storedTask = await db.UserTasks.SingleAsync(row => row.Id == task.Id);
        Assert.Equal(
            terminalWon ? UserTaskStatuses.Completed : UserTaskStatuses.Cancelled,
            storedTask.Status);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent, "completed", ExecutionTokenStatuses.Completed)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent, "faulted", ExecutionTokenStatuses.Faulted)]
    public async Task MessageCatch_EnteringTerminalEventFinalizesTheToken(
        string terminalType,
        string expectedInstanceStatus,
        string expectedTokenStatus)
    {
        var workflowId = await CreateWorkflowAsync(CreateMessageTerminalModel(terminalType));
        var started = await StartAtUserTaskAsync(workflowId, expectedCurrentNodeId: 2);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{started.Id}/message");
        request.Headers.Add("X-Client-Id", "tests-client");
        request.Headers.Add("X-Client-Secret", "tests-secret");
        request.Headers.Add("X-Correlation", "accepted");
        request.Content = JsonContent.Create(new { }, options: JsonOptions);
        using var delivered = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        var ack = await ReadAsync<MessageDeliveryAckDto>(delivered);
        Assert.Equal(expectedInstanceStatus, ack.Status);
        Assert.Equal(3, ack.CurrentNodeId);
        Assert.Equal("terminal-event", ack.CurrentNodeExternalId);
        AssertFault(terminalType, ack.Fault, "MESSAGE_DELIVERY_FAULT", "Terminal");

        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal(expectedInstanceStatus, detail.Status);
        Assert.Equal(3, detail.CurrentNodeId);
        AssertFault(terminalType, detail.Fault, "MESSAGE_DELIVERY_FAULT", "Terminal");
        var messageHistory = Assert.Single(detail.History, entry =>
            entry.Note == "message" && entry.FromNodeId == 2 && entry.ToNodeId == 3);
        Assert.Equal("tests-client", messageHistory.PerformedBy);
        Assert.Equal("message", messageHistory.Note);

        await using var db = fixture.CreateDbContext();
        var token = await db.ExecutionTokens.SingleAsync(row => row.InstanceId == started.Id);
        Assert.Equal(terminalType, token.NodeType);
        Assert.Equal(expectedTokenStatus, token.Status);
        if (terminalType == BpmnFlowNodeTypes.ErrorEndEvent)
        {
            Assert.Equal("MESSAGE_DELIVERY_FAULT", token.FaultCode);
            Assert.Equal("Terminal", token.FaultDescription);
        }
    }

    [Fact]
    public async Task MessageStart_EnteringErrorEndEventReturnsFaultedAckAndPersistsFault()
    {
        var model = CreateMessageStartErrorEndModel();
        await CreateWorkflowAsync(model);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/workflows/" + Uri.EscapeDataString(model.Id) + "/message-start");
        request.Headers.Add("X-Client-Id", "tests-client");
        request.Headers.Add("X-Client-Secret", "tests-secret");
        request.Headers.Add("X-Correlation", "accepted");
        request.Content = JsonContent.Create(new { }, options: JsonOptions);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await ReadAsync<MessageStartAckDto>(response);
        Assert.Equal("faulted", ack.Status);
        Assert.Equal(2, ack.CurrentNodeId);
        var fault = Assert.IsType<FaultInfoDto>(ack.Fault);
        Assert.Equal("MESSAGE_START_FAULT", fault.Code);
        Assert.Equal("The inbound message was rejected.", fault.Description);

        var detail = await GetInstanceAsync(ack.InstanceId);
        Assert.Equal("faulted", detail.Status);
        Assert.Equal("MESSAGE_START_FAULT", Assert.IsType<FaultInfoDto>(detail.Fault).Code);
        Assert.Single(detail.History, entry => entry.Note == "messageStart" && entry.ToNodeId == 2);

        await using var db = fixture.CreateDbContext();
        var token = await db.ExecutionTokens.SingleAsync(row => row.InstanceId == ack.InstanceId);
        Assert.Equal(ExecutionTokenStatuses.Faulted, token.Status);
        Assert.Equal("MESSAGE_START_FAULT", token.FaultCode);
        Assert.Equal("The inbound message was rejected.", token.FaultDescription);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.ServiceTask)]
    [InlineData(BpmnFlowNodeTypes.ScriptTask)]
    public async Task ErrorBoundary_EnteringErrorEndEventFaultsTheInstance(string hostType)
    {
        var workflowId = await CreateWorkflowAsync(CreateBoundaryErrorEndModel(hostType));
        using var startedResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null));

        Assert.Equal(HttpStatusCode.Created, startedResponse.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(startedResponse);
        Assert.Equal("faulted", detail.Status);
        Assert.Equal(5, detail.CurrentNodeId);
        Assert.Equal("error-terminal", detail.CurrentNodeExternalId);
        var fault = Assert.IsType<FaultInfoDto>(detail.Fault);
        Assert.Equal("HOST_EXECUTION_FAILED", fault.Code);
        Assert.Equal("The automated host task failed.", fault.Description);
        Assert.Contains(detail.History, entry => entry.Note == "error" && entry.ToNodeId == 4);
        Assert.Contains(detail.History, entry =>
            entry.Note == "boundary" && entry.FromNodeId == 4 && entry.ToNodeId == 5);
        Assert.Contains(detail.Variables, variable =>
            variable.VariableName == "caughtError"
            && !string.IsNullOrWhiteSpace(variable.Value.GetString()));
        if (hostType == BpmnFlowNodeTypes.ScriptTask)
        {
            var caughtError = detail.Variables.Last(variable => variable.VariableName == "caughtError");
            Assert.Contains("terminal boundary failure", caughtError.Value.GetString(), StringComparison.OrdinalIgnoreCase);
            var staged = Assert.Single(detail.Variables, variable => variable.VariableName == "staged");
            Assert.Equal("initial", staged.Value.GetString());
            Assert.Null(staged.SourceFlowId);
        }

        await using var db = fixture.CreateDbContext();
        var token = await db.ExecutionTokens.SingleAsync(row => row.InstanceId == detail.Id);
        Assert.Equal(BpmnFlowNodeTypes.ErrorEndEvent, token.NodeType);
        Assert.Equal(ExecutionTokenStatuses.Faulted, token.Status);
        Assert.Equal("HOST_EXECUTION_FAILED", token.FaultCode);
        Assert.Equal("The automated host task failed.", token.FaultDescription);
        Assert.False(await db.UserTasks.AnyAsync(row => row.InstanceId == detail.Id));
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

    private async Task<InstanceDetailDto> StartAtUserTaskAsync(long workflowId, int expectedCurrentNodeId = 2)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal("running", detail.Status);
        Assert.Equal(expectedCurrentNodeId, detail.CurrentNodeId);
        return detail;
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<PagedResult<UserTaskDto>> ListTasksAsync(
        long instanceId,
        string status,
        string user = "test-admin")
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status={status}&page=1&pageSize=20",
            user: user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<UserTaskDto>>(response);
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

    private static void AssertFault(
        string terminalType,
        FaultInfoDto? fault,
        string expectedCode,
        string expectedDescription)
    {
        if (terminalType == BpmnFlowNodeTypes.ErrorEndEvent)
        {
            var present = Assert.IsType<FaultInfoDto>(fault);
            Assert.Equal(expectedCode, present.Code);
            Assert.Equal(expectedDescription, present.Description);
        }
        else
        {
            Assert.Null(fault);
        }
    }

    private static WorkflowModel CreateUserTaskTerminalModel(string terminalType)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "terminal-user-task-" + suffix,
            Name = "Terminal user task " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Terminal",
                    ExternalId = "terminal-event",
                    Type = terminalType,
                    ErrorCode = terminalType == BpmnFlowNodeTypes.ErrorEndEvent ? "USER_TASK_FAULT" : null,
                    ErrorDescription = terminalType == BpmnFlowNodeTypes.ErrorEndEvent
                        ? "The user task ended in a fault."
                        : null
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Enter review", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Finish", SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private static WorkflowModel CreateMessageTerminalModel(string terminalType)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "terminal-message-" + suffix,
            Name = "Terminal message " + suffix,
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
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Terminal",
                    ExternalId = "terminal-event",
                    Type = terminalType,
                    ErrorCode = terminalType == BpmnFlowNodeTypes.ErrorEndEvent ? "MESSAGE_DELIVERY_FAULT" : null
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Wait", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Delivered", SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private static WorkflowModel CreateMessageStartErrorEndModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "terminal-message-start-" + suffix,
            Name = "Terminal message start " + suffix,
            InitialEventId = null,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Inbound message",
                    Type = BpmnFlowNodeTypes.MessageStartEvent,
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
                    Id = 2,
                    Name = "Message rejected",
                    Type = BpmnFlowNodeTypes.ErrorEndEvent,
                    ErrorCode = "MESSAGE_START_FAULT",
                    ErrorDescription = "The inbound message was rejected."
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Reject", SourceRef = 1, TargetRef = 2 }
            ]
        };
    }

    private static WorkflowModel CreateBoundaryErrorEndModel(string hostType)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var host = new FlowNodeModel
        {
            Id = 2,
            Name = "Failing activity",
            Type = hostType
        };
        if (hostType == BpmnFlowNodeTypes.ServiceTask)
        {
            host.Service = new ServiceTaskModel
            {
                Url = "https://tests.local/typed-output-invalid",
                OutputMappings =
                [
                    new ServiceOutputMappingModel
                    {
                        Variable = "score",
                        Path = "result.score",
                        Required = true,
                        DataType = WorkflowVariableTypes.Number,
                        IsArray = false
                    }
                ]
            };
        }
        else
        {
            host.ScriptFormat = ScriptFormats.JavaScript;
            host.UsesFlowInfo = false;
            host.Script = "execution.setVariable('staged', 'must roll back'); " +
                          "throw new Error('terminal boundary failure');";
        }

        return new WorkflowModel
        {
            Id = "terminal-boundary-" + suffix,
            Name = "Terminal boundary " + suffix,
            InitialEventId = 1,
            Variables = hostType == BpmnFlowNodeTypes.ScriptTask
                ?
                [
                    new VariableModel
                    {
                        Id = 1,
                        Name = "staged",
                        DataType = WorkflowVariableTypes.String,
                        DefaultValue = JsonSerializer.SerializeToElement("initial")
                    }
                ]
                : [],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                host,
                new FlowNodeModel { Id = 3, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Catch failure",
                    Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                    AttachedToRef = 2,
                    ErrorVariable = "caughtError"
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Error end",
                    ExternalId = "error-terminal",
                    Type = BpmnFlowNodeTypes.ErrorEndEvent,
                    ErrorCode = "HOST_EXECUTION_FAILED",
                    ErrorDescription = "The automated host task failed."
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Run", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Success", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 401, Name = "Failure", SourceRef = 4, TargetRef = 5 }
            ]
        };
    }
}
