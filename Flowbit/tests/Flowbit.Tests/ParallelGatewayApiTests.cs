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
public sealed class ParallelGatewayApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ActorRoles = ["User"];

    [Fact]
    public async Task ThreeBranchFork_WaitsAtJoin_ReleasesOnce_AndCompletesNormally()
    {
        var workflowId = await CreateWorkflowAsync(CreateParallelWorkflow());
        var started = await StartAsync(workflowId);

        Assert.Equal("running", started.Status);
        AssertPositions(
            started.ExecutionPositions,
            (3, ExecutionTokenStatuses.Active),
            (6, ExecutionTokenStatuses.Active),
            (6, ExecutionTokenStatuses.Active));
        var activeScope = Assert.Single(started.ParallelGatewayExecutions);
        Assert.Equal(2, activeScope.ForkNodeId);
        Assert.Equal(ParallelGatewayExecutionStatuses.Active, activeScope.Status);
        Assert.Equal(3, activeScope.TotalBranchCount);
        Assert.Equal(3, activeScope.ActiveBranchCount);
        Assert.Equal(0, activeScope.CompletedBranchCount);
        Assert.Equal(0, activeScope.MergedBranchCount);
        Assert.Equal(0, activeScope.InterruptedBranchCount);
        Assert.Equal(0, activeScope.CancelledBranchCount);

        var manager = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);
        Assert.Equal(3, manager.NodeId);
        using var continuedResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{manager.Id}/flows/301",
            new TakeFlowRequest(null),
            "manager");
        Assert.Equal(HttpStatusCode.OK, continuedResponse.StatusCode);
        var continued = await ReadAsync<UserTaskActionAckDto>(continuedResponse);
        Assert.Equal("running", continued.InstanceStatus);
        AssertPositions(continued.ExecutionPositions, (7, ExecutionTokenStatuses.Active));
        Assert.Null(continued.Completion);

        var afterJoin = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);
        Assert.Equal(7, afterJoin.NodeId);
        using var completedResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{afterJoin.Id}/flows/701",
            new TakeFlowRequest(null),
            "manager");
        Assert.Equal(HttpStatusCode.OK, completedResponse.StatusCode);
        var completed = await ReadAsync<UserTaskActionAckDto>(completedResponse);
        Assert.Equal("completed", completed.InstanceStatus);
        AssertPositions(completed.ExecutionPositions, (8, ExecutionTokenStatuses.Completed));
        Assert.Null(Assert.Single(completed.ExecutionPositions).UserTaskId);
        var completion = Assert.IsType<CompletionInfoDto>(completed.Completion);
        Assert.Equal(WorkflowCompletionKinds.Normal, completion.Kind);
        Assert.Equal(8, completion.NodeId);

        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal("completed", detail.Status);
        AssertPositions(detail.ExecutionPositions, (8, ExecutionTokenStatuses.Completed));
        var joinedScope = Assert.Single(detail.ParallelGatewayExecutions);
        Assert.Equal(ParallelGatewayExecutionStatuses.Joined, joinedScope.Status);
        Assert.Equal(3, joinedScope.TotalBranchCount);
        Assert.Equal(3, joinedScope.MergedBranchCount);
        Assert.Equal(0, joinedScope.ActiveBranchCount);
        Assert.Equal(WorkflowCompletionKinds.Normal, Assert.IsType<CompletionInfoDto>(detail.Completion).Kind);
        Assert.Equal(3, detail.History.Count(entry => entry.Note == "parallelFork"));
        Assert.Single(detail.History, entry => entry.Note == "parallelJoin");

        await using var db = fixture.CreateDbContext();
        var tokens = await db.ExecutionTokens
            .Where(token => token.InstanceId == started.Id)
            .OrderBy(token => token.Id)
            .ToListAsync();
        Assert.Equal(3, tokens.Count);
        Assert.Single(tokens, token => token.Status == ExecutionTokenStatuses.Completed && token.NodeId == 8);
        Assert.Equal(2, tokens.Count(token => token.Status == ExecutionTokenStatuses.Merged));
        Assert.DoesNotContain(
            detail.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Merged);
    }

    [Fact]
    public async Task ManagerInterrupt_CancelsSiblingTokensAlreadyWaitingAtJoin_AndContinues()
    {
        var workflowId = await CreateWorkflowAsync(CreateParallelWorkflow());
        var started = await StartAsync(workflowId);
        var manager = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{manager.Id}/flows/302",
            new TakeFlowRequest(null),
            "manager");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal("running", ack.InstanceStatus);
        AssertPositions(
            ack.ExecutionPositions,
            (6, ExecutionTokenStatuses.Cancelled),
            (6, ExecutionTokenStatuses.Cancelled),
            (10, ExecutionTokenStatuses.Active));
        Assert.All(
            ack.ExecutionPositions.Where(position => position.TokenStatus == ExecutionTokenStatuses.Cancelled),
            position => Assert.Equal("parallelScopeCancelled", position.TerminationReason));

        var emergency = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);
        Assert.Equal(10, emergency.NodeId);
        var detail = await GetInstanceAsync(started.Id);
        var scope = Assert.Single(detail.ParallelGatewayExecutions);
        Assert.Equal(ParallelGatewayExecutionStatuses.Interrupted, scope.Status);
        Assert.Equal(9, scope.InterruptingNodeId);
        Assert.NotNull(scope.InterruptingTokenId);
        Assert.Equal(3, scope.TotalBranchCount);
        Assert.Equal(1, scope.InterruptedBranchCount);
        Assert.Equal(2, scope.CancelledBranchCount);
        Assert.Single(detail.History, entry => entry.Note == "parallelInterrupt");
        Assert.DoesNotContain(detail.History, entry => entry.Note == "parallelInterruptSkipped");

        await using var db = fixture.CreateDbContext();
        var cancelled = await db.ExecutionTokens
            .Where(token => token.InstanceId == started.Id
                            && token.Status == ExecutionTokenStatuses.Cancelled)
            .ToListAsync();
        Assert.Equal(2, cancelled.Count);
        Assert.All(cancelled, token =>
        {
            Assert.Equal(6, token.NodeId);
            Assert.Equal("parallelScopeCancelled", token.TerminationReason);
        });
    }

    [Fact]
    public async Task InterruptWithoutActiveReferencedScope_IsRecordedAsSkippedAndContinues()
    {
        var workflowId = await CreateWorkflowAsync(CreateParallelWorkflow(includeStaleInterrupt: true));
        var started = await StartAsync(workflowId);
        var manager = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);

        using (var firstResponse = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{manager.Id}/flows/302",
                   new TakeFlowRequest(null),
                   "manager"))
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }

        var staleTrigger = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);
        Assert.Equal(10, staleTrigger.NodeId);
        using var staleResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{staleTrigger.Id}/flows/1001",
            new TakeFlowRequest(null),
            "manager");

        Assert.Equal(HttpStatusCode.OK, staleResponse.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(staleResponse);
        AssertPositions(
            ack.ExecutionPositions,
            (6, ExecutionTokenStatuses.Cancelled),
            (6, ExecutionTokenStatuses.Cancelled),
            (13, ExecutionTokenStatuses.Active));

        var detail = await GetInstanceAsync(started.Id);
        Assert.Single(detail.History, entry => entry.Note == "parallelInterrupt");
        Assert.Single(detail.History, entry => entry.Note == "parallelInterruptSkipped");
        Assert.Equal(
            ParallelGatewayExecutionStatuses.Interrupted,
            Assert.Single(detail.ParallelGatewayExecutions).Status);
    }

    [Fact]
    public async Task InterruptTargetingSameFork_ClosesOldActivationAndCreatesFreshActivation()
    {
        var workflowId = await CreateWorkflowAsync(CreateParallelWorkflow(restartFork: true));
        var started = await StartAsync(workflowId);
        var manager = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{manager.Id}/flows/302",
            new TakeFlowRequest(null),
            "manager");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal("running", ack.InstanceStatus);
        Assert.Equal(3, ack.ExecutionPositions.Count(position =>
            position.TokenStatus == ExecutionTokenStatuses.Active));
        Assert.Equal(2, ack.ExecutionPositions.Count(position =>
            position.TokenStatus == ExecutionTokenStatuses.Cancelled));
        Assert.Contains(
            ack.ExecutionPositions,
            position => position.NodeId == 3 && position.TokenStatus == ExecutionTokenStatuses.Active);
        Assert.Equal(2, ack.ExecutionPositions.Count(position =>
            position.NodeId == 6 && position.TokenStatus == ExecutionTokenStatuses.Active));

        var activeManager = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);
        Assert.Equal(3, activeManager.NodeId);
        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal(2, detail.ParallelGatewayExecutions.Count);
        Assert.Single(
            detail.ParallelGatewayExecutions,
            execution => execution.Status == ParallelGatewayExecutionStatuses.Interrupted);
        Assert.Single(
            detail.ParallelGatewayExecutions,
            execution => execution.Status == ParallelGatewayExecutionStatuses.Active);
        Assert.Equal(6, detail.History.Count(entry => entry.Note == "parallelFork"));
        Assert.Single(detail.History, entry => entry.Note == "parallelInterrupt");
    }

    [Fact]
    public async Task TerminateEnd_CompletesInstanceAndCancelsEveryOtherParallelBranch()
    {
        var workflowId = await CreateWorkflowAsync(CreateParallelWorkflow());
        var started = await StartAsync(workflowId);
        var manager = Assert.Single((await ListTasksAsync(started.Id, "active")).Items);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{manager.Id}/flows/303",
            new TakeFlowRequest(null),
            "manager");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal("completed", ack.InstanceStatus);
        AssertPositions(
            ack.ExecutionPositions,
            (6, ExecutionTokenStatuses.Cancelled),
            (6, ExecutionTokenStatuses.Cancelled),
            (11, ExecutionTokenStatuses.Completed));
        var completion = Assert.IsType<CompletionInfoDto>(ack.Completion);
        Assert.Equal(WorkflowCompletionKinds.Terminate, completion.Kind);
        Assert.Equal(11, completion.NodeId);
        Assert.Null(Assert.Single(
            ack.ExecutionPositions,
            position => position.NodeId == 11).UserTaskId);
        Assert.All(
            ack.ExecutionPositions.Where(position => position.TokenStatus == ExecutionTokenStatuses.Cancelled),
            position => Assert.Equal("terminateEnd", position.TerminationReason));

        var detail = await GetInstanceAsync(started.Id);
        Assert.Equal("completed", detail.Status);
        Assert.Equal(WorkflowCompletionKinds.Terminate, Assert.IsType<CompletionInfoDto>(detail.Completion).Kind);
        var terminatedScope = Assert.Single(detail.ParallelGatewayExecutions);
        Assert.Equal(ParallelGatewayExecutionStatuses.Cancelled, terminatedScope.Status);
        Assert.Equal(3, terminatedScope.TotalBranchCount);
        Assert.Equal(1, terminatedScope.CompletedBranchCount);
        Assert.Equal(2, terminatedScope.CancelledBranchCount);
        Assert.Empty((await ListTasksAsync(started.Id, "active")).Items);

        using var duplicate = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{manager.Id}/flows/303",
            new TakeFlowRequest(null),
            "manager");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task InstanceSummary_ProjectsEveryNonMergedBranchPosition()
    {
        var workflowId = await CreateWorkflowAsync(CreateParallelWorkflow());
        var started = await StartAsync(workflowId);

        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances?instanceId={started.Id}&page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = Assert.Single((await ReadAsync<PagedResult<InstanceSummaryDto>>(response)).Items);
        AssertPositions(
            summary.ExecutionPositions,
            (3, ExecutionTokenStatuses.Active),
            (6, ExecutionTokenStatuses.Active),
            (6, ExecutionTokenStatuses.Active));
        Assert.Null(summary.Completion);
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
            $"/api/instances/{instanceId}/user-tasks?status={status}&page=1&pageSize=20");
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

    private static void AssertPositions(
        IReadOnlyList<ExecutionPositionDto> actual,
        params (int NodeId, string Status)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        Assert.Equal(
            expected.OrderBy(item => item.NodeId).ThenBy(item => item.Status),
            actual.Select(item => (item.NodeId, item.TokenStatus))
                .OrderBy(item => item.NodeId)
                .ThenBy(item => item.TokenStatus));
        Assert.Equal(
            actual.Select(position => position.TokenId).OrderBy(id => id),
            actual.Select(position => position.TokenId));
        Assert.DoesNotContain(
            actual,
            position => position.TokenStatus == ExecutionTokenStatuses.Merged);
    }

    internal static WorkflowModel CreateParallelWorkflow(
        bool includeStaleInterrupt = false,
        bool restartFork = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var nodes = new List<FlowNodeModel>
        {
            new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new() { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
            new() { Id = 3, Name = "Manager", ExternalId = "manager", Type = BpmnFlowNodeTypes.UserTask },
            new() { Id = 4, Name = "Finance", Type = BpmnFlowNodeTypes.Task },
            new() { Id = 5, Name = "Legal", Type = BpmnFlowNodeTypes.Task },
            new() { Id = 6, Name = "Join", Type = BpmnFlowNodeTypes.ParallelGateway },
            new() { Id = 7, Name = "After join", ExternalId = "after-join", Type = BpmnFlowNodeTypes.UserTask },
            new() { Id = 8, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent },
            new()
            {
                Id = 9,
                Name = "Manager interrupt",
                Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                ParallelGatewayRef = 2
            },
            new()
            {
                Id = 10,
                Name = includeStaleInterrupt ? "Trigger stale interrupt" : "Emergency review",
                ExternalId = includeStaleInterrupt ? "stale-trigger" : "emergency-review",
                Type = BpmnFlowNodeTypes.UserTask
            },
            new() { Id = 11, Name = "Terminate", Type = BpmnFlowNodeTypes.TerminateEndEvent }
        };
        var flows = new List<SequenceFlowModel>
        {
            new() { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, Name = "Manager", SourceRef = 2, TargetRef = 3 },
            new() { Id = 202, Name = "Finance", SourceRef = 2, TargetRef = 4 },
            new() { Id = 203, Name = "Legal", SourceRef = 2, TargetRef = 5 },
            new() { Id = 301, Name = "Continue", SourceRef = 3, TargetRef = 6 },
            new() { Id = 302, Name = "Override", SourceRef = 3, TargetRef = 9 },
            new() { Id = 303, Name = "Terminate", SourceRef = 3, TargetRef = 11 },
            new() { Id = 401, Name = "Finance complete", SourceRef = 4, TargetRef = 6 },
            new() { Id = 501, Name = "Legal complete", SourceRef = 5, TargetRef = 6 },
            new() { Id = 601, Name = "Joined", SourceRef = 6, TargetRef = 7 },
            new() { Id = 701, Name = "Complete", SourceRef = 7, TargetRef = 8 },
            new()
            {
                Id = 901,
                Name = restartFork ? "Restart fork" : "Continue after interrupt",
                SourceRef = 9,
                TargetRef = restartFork ? 2 : 10
            }
        };

        if (includeStaleInterrupt)
        {
            nodes.Add(new FlowNodeModel
            {
                Id = 12,
                Name = "Stale interrupt",
                Type = BpmnFlowNodeTypes.ParallelInterruptEvent,
                ParallelGatewayRef = 2
            });
            nodes.Add(new FlowNodeModel
            {
                Id = 13,
                Name = "After stale interrupt",
                ExternalId = "after-stale",
                Type = BpmnFlowNodeTypes.UserTask
            });
            flows.Add(new SequenceFlowModel
            {
                Id = 1001,
                Name = "Try interrupt again",
                SourceRef = 10,
                TargetRef = 12
            });
            flows.Add(new SequenceFlowModel
            {
                Id = 1201,
                Name = "Continue",
                SourceRef = 12,
                TargetRef = 13
            });
            flows.Add(new SequenceFlowModel
            {
                Id = 1301,
                Name = "Finish",
                SourceRef = 13,
                TargetRef = 8
            });
        }
        else
        {
            flows.Add(new SequenceFlowModel
            {
                Id = 1001,
                Name = "Finish emergency review",
                SourceRef = 10,
                TargetRef = 8
            });
        }

        return new WorkflowModel
        {
            Id = "parallel-api-" + suffix,
            Name = "Parallel API " + suffix,
            InitialEventId = 1,
            FlowNodes = nodes,
            SequenceFlows = flows
        };
    }
}
