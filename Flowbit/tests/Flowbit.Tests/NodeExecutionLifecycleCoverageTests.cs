using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class NodeExecutionLifecycleCoverageTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AutomaticTaskAndExclusiveGatewayPersistDistinctCompletedVisits()
    {
        var workflowId = await CreateWorkflowAsync(CreateAutomaticGatewayModel());

        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "route-starter",
            ["Operator"],
            suppressDefaultAdmin: true);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var instance = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal(WorkflowInstanceStatuses.Completed, instance.Status);

        await using var db = fixture.CreateDbContext();
        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(execution => execution.InstanceId == instance.Id)
            .OrderBy(execution => execution.Id)
            .ToListAsync();

        Assert.Equal(4, executions.Count);
        Assert.Single(executions.Select(execution => execution.ExecutionTokenId).Distinct());
        Assert.DoesNotContain(executions, execution => execution.NodeId == 5);

        var start = Assert.Single(executions, execution => execution.NodeId == 1);
        AssertCompleted(start, NodeExecutionCompletionReasons.Normal, "route-starter");
        Assert.Null(start.EnteredViaFlowId);
        Assert.Null(start.SelectedFlowId);
        Assert.Equal(101, start.ExitedViaFlowId);

        var task = Assert.Single(executions, execution => execution.NodeId == 2);
        Assert.Equal(BpmnFlowNodeTypes.Task, task.NodeType);
        AssertCompleted(task, NodeExecutionCompletionReasons.Normal, "route-starter");
        Assert.Equal(101, task.EnteredViaFlowId);
        Assert.Null(task.SelectedFlowId);
        Assert.Equal(201, task.ExitedViaFlowId);

        var gateway = Assert.Single(executions, execution => execution.NodeId == 3);
        Assert.Equal(BpmnFlowNodeTypes.ExclusiveGateway, gateway.NodeType);
        AssertCompleted(gateway, NodeExecutionCompletionReasons.Normal, "route-starter");
        Assert.Equal(201, gateway.EnteredViaFlowId);
        Assert.Equal(301, gateway.SelectedFlowId);
        Assert.Equal(301, gateway.ExitedViaFlowId);

        var end = Assert.Single(executions, execution => execution.NodeId == 4);
        AssertCompleted(end, NodeExecutionCompletionReasons.NormalEnd, "route-starter");
        Assert.Equal(301, end.EnteredViaFlowId);
        Assert.Null(end.ExitedViaFlowId);

        Assert.All(executions, execution =>
        {
            Assert.Equal(["Operator"], StringList(execution.TriggeredByRolesJson));
            Assert.Equal(["Operator"], StringList(execution.CompletedByRolesJson));
        });

        var token = await db.ExecutionTokens.AsNoTracking()
            .SingleAsync(candidate => candidate.InstanceId == instance.Id);
        Assert.Equal(ExecutionTokenStatuses.Completed, token.Status);
        Assert.Null(token.CurrentNodeExecutionId);
    }

    [Fact]
    public async Task MessageStartAndCatchTrackWaitDeliveryActorsAndLocalVariableWrites()
    {
        var model = CreateMessageLifecycleModel();
        await CreateWorkflowAsync(model);

        using var startRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/workflows/{Uri.EscapeDataString(model.Id)}/message-start")
        {
            Content = JsonContent.Create(new { startValue = "opened" }, options: JsonOptions)
        };
        AddMessageHeaders(startRequest);
        using var startResponse = await fixture.Client.SendAsync(startRequest);

        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        var started = await ReadAsync<MessageStartAckDto>(startResponse);
        Assert.Equal(2, started.CurrentNodeId);
        Assert.Equal(WorkflowInstanceStatuses.Running, started.Status);

        long startExecutionId;
        long catchExecutionId;
        await using (var db = fixture.CreateDbContext())
        {
            var executions = await db.NodeExecutions.AsNoTracking()
                .Where(execution => execution.InstanceId == started.InstanceId)
                .OrderBy(execution => execution.Id)
                .ToListAsync();
            Assert.Equal(2, executions.Count);

            var messageStart = Assert.Single(executions, execution => execution.NodeId == 1);
            Assert.Equal(BpmnFlowNodeTypes.MessageStartEvent, messageStart.NodeType);
            AssertCompleted(
                messageStart,
                NodeExecutionCompletionReasons.MessageDelivery,
                "tests-client");
            Assert.Null(messageStart.EnteredViaFlowId);
            Assert.Equal(101, messageStart.ExitedViaFlowId);
            Assert.Empty(StringList(messageStart.TriggeredByRolesJson));
            Assert.Empty(StringList(messageStart.CompletedByRolesJson));
            startExecutionId = messageStart.Id;

            var messageCatch = Assert.Single(executions, execution => execution.NodeId == 2);
            Assert.Equal(BpmnFlowNodeTypes.IntermediateMessageCatchEvent, messageCatch.NodeType);
            Assert.Equal(NodeExecutionStatuses.Active, messageCatch.Status);
            Assert.Null(messageCatch.CompletionReason);
            Assert.Equal(101, messageCatch.EnteredViaFlowId);
            Assert.Equal("tests-client", messageCatch.TriggeredBy);
            Assert.Null(messageCatch.CompletedAt);
            catchExecutionId = messageCatch.Id;

            var token = await db.ExecutionTokens.AsNoTracking()
                .SingleAsync(candidate => candidate.InstanceId == started.InstanceId);
            Assert.Equal(catchExecutionId, token.CurrentNodeExecutionId);

            var startVariable = await db.InstanceVariables.AsNoTracking()
                .SingleAsync(variable =>
                    variable.InstanceId == started.InstanceId
                    && variable.VariableName == "startValue");
            Assert.Equal(startExecutionId, startVariable.NodeExecutionId);
        }

        using var deliveryRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/instances/{started.InstanceId}/message")
        {
            Content = JsonContent.Create(new { reply = "accepted" }, options: JsonOptions)
        };
        AddMessageHeaders(deliveryRequest);
        using var deliveryResponse = await fixture.Client.SendAsync(deliveryRequest);

        Assert.Equal(HttpStatusCode.OK, deliveryResponse.StatusCode);
        var delivered = await ReadAsync<MessageDeliveryAckDto>(deliveryResponse);
        Assert.Equal(WorkflowInstanceStatuses.Completed, delivered.Status);
        Assert.Equal(3, delivered.CurrentNodeId);

        await using (var db = fixture.CreateDbContext())
        {
            var executions = await db.NodeExecutions.AsNoTracking()
                .Where(execution => execution.InstanceId == started.InstanceId)
                .OrderBy(execution => execution.Id)
                .ToListAsync();
            Assert.Equal(3, executions.Count);

            var messageCatch = Assert.Single(executions, execution => execution.NodeId == 2);
            Assert.Equal(catchExecutionId, messageCatch.Id);
            AssertCompleted(
                messageCatch,
                NodeExecutionCompletionReasons.MessageDelivery,
                "tests-client");
            Assert.Equal(101, messageCatch.EnteredViaFlowId);
            Assert.Equal(201, messageCatch.ExitedViaFlowId);

            var end = Assert.Single(executions, execution => execution.NodeId == 3);
            AssertCompleted(end, NodeExecutionCompletionReasons.NormalEnd, "tests-client");
            Assert.Equal(201, end.EnteredViaFlowId);

            var replyVariable = await db.InstanceVariables.AsNoTracking()
                .SingleAsync(variable =>
                    variable.InstanceId == started.InstanceId
                    && variable.VariableName == "reply");
            Assert.Equal(catchExecutionId, replyVariable.NodeExecutionId);

            var token = await db.ExecutionTokens.AsNoTracking()
                .SingleAsync(candidate => candidate.InstanceId == started.InstanceId);
            Assert.Equal(ExecutionTokenStatuses.Completed, token.Status);
            Assert.Null(token.CurrentNodeExecutionId);
        }
    }

    [Fact]
    public async Task ParallelInterruptAndStaleInterruptCloseVisitsWithPreciseReasons()
    {
        var workflowId = await CreateWorkflowAsync(
            ParallelGatewayApiTests.CreateParallelWorkflow(includeStaleInterrupt: true));
        var instance = await StartInstanceAsync(workflowId, "manager", ["User"]);

        var managerTaskId = await FindActiveTaskAsync(instance.Id, 3);
        using (var response = await SendAuthorizedAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{managerTaskId}/flows/302",
                   new TakeFlowRequest(null),
                   "manager",
                   ["User"]))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        long interruptExecutionId;
        await using (var db = fixture.CreateDbContext())
        {
            var interrupt = await db.NodeExecutions.AsNoTracking()
                .SingleAsync(execution =>
                    execution.InstanceId == instance.Id
                    && execution.NodeId == 9);
            AssertCompleted(
                interrupt,
                NodeExecutionCompletionReasons.ParallelInterrupt,
                "manager");
            Assert.Equal(302, interrupt.EnteredViaFlowId);
            Assert.Equal(901, interrupt.ExitedViaFlowId);
            Assert.NotNull(interrupt.EntryParallelBranchId);
            Assert.Null(interrupt.ExitParallelBranchId);
            interruptExecutionId = interrupt.Id;

            var cancelledJoinVisits = await db.NodeExecutions.AsNoTracking()
                .Where(execution =>
                    execution.InstanceId == instance.Id
                    && execution.NodeId == 6
                    && execution.Status == NodeExecutionStatuses.Cancelled)
                .OrderBy(execution => execution.Id)
                .ToListAsync();
            Assert.Equal(2, cancelledJoinVisits.Count);
            Assert.All(cancelledJoinVisits, execution =>
            {
                Assert.Equal(
                    NodeExecutionCompletionReasons.ParallelScopeCancelled,
                    execution.CompletionReason);
                Assert.Equal("manager", execution.CompletedBy);
                Assert.Null(execution.ExitedViaFlowId);
            });
        }

        var staleTriggerTaskId = await FindActiveTaskAsync(instance.Id, 10);
        using (var response = await SendAuthorizedAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{staleTriggerTaskId}/flows/1001",
                   new TakeFlowRequest(null),
                   "manager",
                   ["User"]))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var skipped = await db.NodeExecutions.AsNoTracking()
                .SingleAsync(execution =>
                    execution.InstanceId == instance.Id
                    && execution.NodeId == 12);
            AssertCompleted(
                skipped,
                NodeExecutionCompletionReasons.ParallelInterruptSkipped,
                "manager");
            Assert.Equal(1001, skipped.EnteredViaFlowId);
            Assert.Equal(1201, skipped.ExitedViaFlowId);
            Assert.Null(skipped.EntryParallelBranchId);
            Assert.Null(skipped.ExitParallelBranchId);

            var afterStale = await db.NodeExecutions.AsNoTracking()
                .SingleAsync(execution =>
                    execution.InstanceId == instance.Id
                    && execution.NodeId == 13);
            Assert.Equal(NodeExecutionStatuses.Active, afterStale.Status);
            Assert.Equal(1201, afterStale.EnteredViaFlowId);

            Assert.Equal(2, await db.NodeExecutions.CountAsync(execution =>
                execution.InstanceId == instance.Id
                && execution.Status == NodeExecutionStatuses.Cancelled
                && execution.CompletionReason
                    == NodeExecutionCompletionReasons.ParallelScopeCancelled));
            Assert.Equal(interruptExecutionId, await db.NodeExecutions
                .Where(execution =>
                    execution.InstanceId == instance.Id
                    && execution.CompletionReason
                        == NodeExecutionCompletionReasons.ParallelInterrupt)
                .Select(execution => execution.Id)
                .SingleAsync());
        }
    }

    [Fact]
    public async Task TerminateEndCancelsEverySiblingVisitWithTerminateReason()
    {
        var workflowId = await CreateWorkflowAsync(
            ParallelGatewayApiTests.CreateParallelWorkflow());
        var instance = await StartInstanceAsync(workflowId, "manager", ["User"]);
        var managerTaskId = await FindActiveTaskAsync(instance.Id, 3);

        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{managerTaskId}/flows/303",
            new TakeFlowRequest(null),
            "manager",
            ["User"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(execution => execution.InstanceId == instance.Id)
            .OrderBy(execution => execution.Id)
            .ToListAsync();

        var terminate = Assert.Single(executions, execution => execution.NodeId == 11);
        AssertCompleted(terminate, NodeExecutionCompletionReasons.TerminateEnd, "manager");
        Assert.Equal(303, terminate.EnteredViaFlowId);
        Assert.Equal(BpmnFlowNodeTypes.TerminateEndEvent, terminate.NodeType);

        var cancelledSiblings = executions
            .Where(execution =>
                execution.NodeId == 6
                && execution.Status == NodeExecutionStatuses.Cancelled)
            .ToList();
        Assert.Equal(2, cancelledSiblings.Count);
        Assert.All(cancelledSiblings, execution =>
        {
            Assert.Equal(NodeExecutionCompletionReasons.TerminateEnd, execution.CompletionReason);
            Assert.Equal("manager", execution.CompletedBy);
            Assert.Null(execution.ExitedViaFlowId);
        });
        Assert.DoesNotContain(
            executions,
            execution => execution.Status is NodeExecutionStatuses.Active or NodeExecutionStatuses.Pending);

        var tokens = await db.ExecutionTokens.AsNoTracking()
            .Where(token => token.InstanceId == instance.Id)
            .ToListAsync();
        Assert.Single(tokens, token =>
            token.NodeId == 11 && token.Status == ExecutionTokenStatuses.Completed);
        Assert.Equal(2, tokens.Count(token => token.Status == ExecutionTokenStatuses.Cancelled));
        Assert.All(tokens, token => Assert.Null(token.CurrentNodeExecutionId));
    }

    [Fact]
    public async Task ErrorEndCancelsAlreadyOpenSiblingVisitAndFaultsItsOwnVisit()
    {
        var workflowId = await CreateWorkflowAsync(CreateParallelErrorEndModel());

        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "fault-starter",
            ["Operator"],
            suppressDefaultAdmin: true);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var instance = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal(WorkflowInstanceStatuses.Faulted, instance.Status);

        await using var db = fixture.CreateDbContext();
        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(execution => execution.InstanceId == instance.Id)
            .OrderBy(execution => execution.Id)
            .ToListAsync();

        var waitingSibling = Assert.Single(executions, execution => execution.NodeId == 3);
        Assert.Equal(NodeExecutionStatuses.Cancelled, waitingSibling.Status);
        Assert.Equal(NodeExecutionCompletionReasons.ErrorEnd, waitingSibling.CompletionReason);
        Assert.Equal("fault-starter", waitingSibling.CompletedBy);
        Assert.NotNull(waitingSibling.CompletedAt);

        var automatic = Assert.Single(executions, execution => execution.NodeId == 4);
        AssertCompleted(automatic, NodeExecutionCompletionReasons.Normal, "fault-starter");
        Assert.Equal(401, automatic.ExitedViaFlowId);

        var errorEnd = Assert.Single(executions, execution => execution.NodeId == 5);
        Assert.Equal(NodeExecutionStatuses.Faulted, errorEnd.Status);
        Assert.Equal(NodeExecutionCompletionReasons.ErrorEnd, errorEnd.CompletionReason);
        Assert.Equal("PARALLEL_FAILURE", errorEnd.ErrorCode);
        Assert.Equal("The automatic branch faulted.", errorEnd.ErrorDescription);
        Assert.Equal("fault-starter", errorEnd.TriggeredBy);
        Assert.Equal("fault-starter", errorEnd.CompletedBy);
        Assert.Equal(401, errorEnd.EnteredViaFlowId);

        var task = await db.UserTasks.AsNoTracking()
            .SingleAsync(candidate =>
                candidate.InstanceId == instance.Id
                && candidate.NodeId == 3);
        Assert.Equal(UserTaskStatuses.Cancelled, task.Status);
        Assert.Equal(task.Id, waitingSibling.UserTaskId);

        var tokens = await db.ExecutionTokens.AsNoTracking()
            .Where(token => token.InstanceId == instance.Id)
            .OrderBy(token => token.Id)
            .ToListAsync();
        Assert.Single(tokens, token =>
            token.NodeId == 3
            && token.Status == ExecutionTokenStatuses.Cancelled
            && token.TerminationReason == ExecutionTokenTerminationReasons.ErrorEnd);
        Assert.Single(tokens, token =>
            token.NodeId == 5
            && token.Status == ExecutionTokenStatuses.Faulted
            && token.TerminationReason == ExecutionTokenTerminationReasons.ErrorEnd);
        Assert.All(tokens, token => Assert.Null(token.CurrentNodeExecutionId));
        Assert.DoesNotContain(
            executions,
            execution => execution.Status is NodeExecutionStatuses.Active or NodeExecutionStatuses.Pending);
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartInstanceAsync(
        long workflowId,
        string user,
        string[] roles)
    {
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            user,
            roles);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<long> FindActiveTaskAsync(long instanceId, int nodeId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.AsNoTracking()
            .Where(task =>
                task.InstanceId == instanceId
                && task.NodeId == nodeId
                && task.Status == UserTaskStatuses.Active)
            .Select(task => task.Id)
            .SingleAsync();
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null,
        bool suppressDefaultAdmin = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? []);
        if (suppressDefaultAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static void AddMessageHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-Client-Id", "tests-client");
        request.Headers.Add("X-Client-Secret", "tests-secret");
        request.Headers.Add("X-Correlation", "accepted");
    }

    private static void AssertCompleted(
        NodeExecutionEntity execution,
        string reason,
        string actor)
    {
        Assert.Equal(NodeExecutionStatuses.Completed, execution.Status);
        Assert.Equal(reason, execution.CompletionReason);
        Assert.Equal(actor, execution.TriggeredBy);
        Assert.Equal(actor, execution.CompletedBy);
        Assert.NotNull(execution.StartedAt);
        Assert.NotNull(execution.CompletedAt);
        Assert.True(execution.CompletedAt >= execution.StartedAt);
    }

    private static IReadOnlyList<string> StringList(JsonDocument? document) =>
        document?.RootElement.EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList()
        ?? [];

    private static WorkflowModel CreateAutomaticGatewayModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "node-execution-routing-" + suffix,
            Name = "Node execution routing " + suffix,
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
                    Name = "Prepare",
                    Type = BpmnFlowNodeTypes.Task
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Route",
                    Type = BpmnFlowNodeTypes.ExclusiveGateway
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Selected end",
                    Type = BpmnFlowNodeTypes.EndEvent
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Default end",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel
                {
                    Id = 301,
                    SourceRef = 3,
                    TargetRef = 4,
                    Condition = "1 == 1",
                    ConditionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 302,
                    SourceRef = 3,
                    TargetRef = 5,
                    IsDefault = true
                }
            ]
        };
    }

    private static WorkflowModel CreateMessageLifecycleModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "node-execution-message-" + suffix,
            Name = "Node execution message " + suffix,
            InitialEventId = null,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Message start",
                    ExternalId = "message-start",
                    Type = BpmnFlowNodeTypes.MessageStartEvent,
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
                                Variable = "startValue",
                                Path = "startValue",
                                DataType = WorkflowVariableTypes.String,
                                Required = true
                            }
                        ]
                    }
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Wait for reply",
                    ExternalId = "wait-for-reply",
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
                                Variable = "reply",
                                Path = "reply",
                                DataType = WorkflowVariableTypes.String,
                                Required = true
                            }
                        ]
                    }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "End",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private static WorkflowModel CreateParallelErrorEndModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "node-execution-parallel-error-" + suffix,
            Name = "Node execution parallel error " + suffix,
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
                    Name = "Waiting sibling",
                    Type = BpmnFlowNodeTypes.UserTask
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Automatic fault path",
                    Type = BpmnFlowNodeTypes.Task
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Error end",
                    Type = BpmnFlowNodeTypes.ErrorEndEvent,
                    ErrorCode = "PARALLEL_FAILURE",
                    ErrorDescription = "The automatic branch faulted."
                },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Unreachable end",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 6 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 }
            ]
        };
    }
}
