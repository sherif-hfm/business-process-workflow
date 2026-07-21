using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class TaskAssignmentApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ManagerRole = "AssignmentManager";

    [Fact]
    public async Task AssignmentManagerCanSeeAndAssignTaskHiddenFromRegularInboxes()
    {
        var model = CreateSimpleModel("manager-required-assignment");
        model.TaskDistribution = new TaskDistributionModel
        {
            ClientId = "distributor",
            ClientSecret = "secret"
        };
        model.FlowNodes.Single(node => node.Id == 2).RequiresAssignment = true;
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId);

        Assert.Empty((await GetInboxAsync(instance.Id, "alice", "Worker")).Items);
        var hidden = Assert.Single((await GetManagedAsync(instance.Id)).Items);
        Assert.True(hidden.RequiresAssignment);
        Assert.Equal(UserTaskOwnershipKinds.Unassigned, hidden.Ownership);

        var assigned = await AssignAsync(hidden, "alice", null);
        Assert.True(assigned.RequiresAssignment);
        Assert.Single((await GetInboxAsync(instance.Id, "alice", "Worker")).Items);
    }

    [Fact]
    public async Task ManagerCanAssignReassignReleaseAndAuditAnActiveTask()
    {
        var workflowId = await CreateWorkflowAsync(CreateSimpleModel("assignment-lifecycle"));
        var instance = await StartAsync(workflowId);
        var outsider = await GetManagedAsync(instance.Id, "outsider", "Worker");
        Assert.Empty(outsider.Items);

        var initial = Assert.Single((await GetManagedAsync(instance.Id)).Items);
        Assert.Equal(UserTaskOwnershipKinds.Unassigned, initial.Ownership);

        using var forbidden = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{initial.UserTaskId}/assign",
            new AssignUserTaskRequest("bob", initial.UpdatedAt, null),
            "outsider",
            "Worker");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var assigned = await AssignAsync(initial, "bob", "Initial coverage");
        Assert.True(assigned.Changed);
        Assert.Equal(UserTaskAssignmentOperations.Assigned, assigned.Operation);
        Assert.Equal(UserTaskOwnershipKinds.Assigned, assigned.CurrentOwnership);

        using var stale = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{initial.UserTaskId}/assign",
            new AssignUserTaskRequest("carol", initial.UpdatedAt, null));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var bobTask = Assert.Single((await GetManagedAsync(instance.Id)).Items);
        Assert.Equal("bob", bobTask.Owner);
        var reassigned = await AssignAsync(bobTask, "carol", "Bob is unavailable");
        Assert.Equal(UserTaskAssignmentOperations.Reassigned, reassigned.Operation);

        using var retryResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{bobTask.UserTaskId}/assign",
            new AssignUserTaskRequest("CAROL", bobTask.UpdatedAt, "retry"));
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        var retry = await ReadAsync<UserTaskAssignmentAckDto>(retryResponse);
        Assert.False(retry.Changed);
        Assert.Equal(UserTaskAssignmentOperations.Unchanged, retry.Operation);

        Assert.Empty((await GetInboxAsync(instance.Id, "bob", "Worker")).Items);
        Assert.Single((await GetInboxAsync(instance.Id, "carol", "Worker")).Items);

        var carolTask = Assert.Single((await GetManagedAsync(instance.Id)).Items);
        using var releaseResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{carolTask.UserTaskId}/unassign",
            new UnassignUserTaskRequest(carolTask.UpdatedAt, null));
        Assert.Equal(HttpStatusCode.OK, releaseResponse.StatusCode);
        var released = await ReadAsync<UserTaskAssignmentAckDto>(releaseResponse);
        Assert.Equal(UserTaskAssignmentOperations.Unassigned, released.Operation);
        Assert.False(released.RequiresClaim);
        Assert.Single((await GetInboxAsync(instance.Id, "dana", "Worker")).Items);

        var detail = await GetInstanceAsync(instance.Id);
        var assignmentHistory = detail.History.Where(row => row.Note == "taskAssignment").ToList();
        Assert.Equal(3, assignmentHistory.Count);
        Assert.All(assignmentHistory, row =>
        {
            Assert.Null(row.SequenceFlowId);
            Assert.Equal(row.FromNodeId, row.ToNodeId);
            Assert.Equal(initial.UserTaskId, row.UserTaskId);
            Assert.Equal("manager", row.PerformedBy);
        });
        Assert.Equal("Bob is unavailable",
            assignmentHistory[1].Payload!["reason"].GetString());
    }

    [Fact]
    public async Task ManagerAssignmentConvertsAClaimAndUnassignRestoresClaimRequirement()
    {
        var workflowId = await CreateWorkflowAsync(CreateSimpleModel("claim-conversion", requiresClaim: true));
        var instance = await StartAsync(workflowId);
        var initial = Assert.Single((await GetManagedAsync(instance.Id)).Items);

        using var claimResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{initial.UserTaskId}/claim",
            null,
            "alice",
            "Worker");
        Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);

        var claimed = Assert.Single((await GetManagedAsync(instance.Id)).Items);
        Assert.Equal(UserTaskOwnershipKinds.Claimed, claimed.Ownership);
        Assert.Equal("alice", claimed.Owner);
        var assigned = await AssignAsync(claimed, "bob", null);
        Assert.Equal(UserTaskOwnershipKinds.Assigned, assigned.CurrentOwnership);
        Assert.False(assigned.RequiresClaim);

        var direct = Assert.Single((await GetManagedAsync(instance.Id)).Items);
        using var releaseResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{direct.UserTaskId}/unassign",
            new UnassignUserTaskRequest(direct.UpdatedAt, "Return to pool"));
        Assert.Equal(HttpStatusCode.OK, releaseResponse.StatusCode);
        var released = await ReadAsync<UserTaskAssignmentAckDto>(releaseResponse);
        Assert.True(released.RequiresClaim);

        var inbox = await GetInboxAsync(instance.Id, "bob", "Worker");
        var item = Assert.Single(inbox.Items);
        Assert.True(item.CanClaim);
        Assert.Null(item.Assignee);
        Assert.Null(item.ClaimedBy);
    }

    [Fact]
    public async Task OnePerActorRejectsAssigningTwoActiveItemsToTheSameActor()
    {
        var model = DefinitionValidationTests.LoadModel("votes-cardinality-approve-reject.json");
        MakeUnique(model, "assignment-one-per-actor");
        model.TaskAssignmentRoles = [ManagerRole];
        var node = model.FlowNodes.Single(flowNode => flowNode.Id == 2);
        node.RequiresClaim = true;
        node.MultiInstance!.OnePerActor = true;
        var workflowId = await CreateWorkflowAsync(model);
        var review = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["voters"] = JsonSerializer.SerializeToElement(2)
        });
        using var enterResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{review.Id}/flows/204",
            new TakeFlowRequest(null),
            "reviewer",
            "Manager",
            "User");
        Assert.Equal(HttpStatusCode.OK, enterResponse.StatusCode);

        var tasks = (await GetManagedAsync(review.Id)).Items.OrderBy(item => item.ItemIndex).ToList();
        Assert.Equal(2, tasks.Count);
        await AssignAsync(tasks[0], "voter", null);
        using var conflict = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{tasks[1].UserTaskId}/assign",
            new AssignUserTaskRequest("VOTER", tasks[1].UpdatedAt, null));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    private async Task<UserTaskAssignmentAckDto> AssignAsync(
        ManagedUserTaskDto task,
        string actorId,
        string? reason)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.UserTaskId}/assign",
            new AssignUserTaskRequest(actorId, task.UpdatedAt, reason));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UserTaskAssignmentAckDto>(response);
    }

    private async Task<PagedResult<ManagedUserTaskDto>> GetManagedAsync(
        long instanceId,
        string user = "manager",
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/user-tasks/manage?instanceId={instanceId}&pageSize=200",
            null,
            user,
            roles.Length == 0 ? [ManagerRole] : roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<ManagedUserTaskDto>>(response);
    }

    private async Task<PagedResult<InboxItemDto>> GetInboxAsync(
        long instanceId,
        string user,
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/inbox?instanceId={instanceId}&pageSize=200",
            null,
            user,
            roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<InboxItemDto>>(response);
    }

    private async Task<InstanceDetailDto> StartAsync(
        long workflowId,
        Dictionary<string, JsonElement>? variables = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables),
            "starter",
            "Worker",
            "User");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
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

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "manager",
        params string[] roles)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, user, roles.Length == 0 ? [ManagerRole] : roles);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateSimpleModel(
        string label,
        bool requiresClaim = false,
        string? assignee = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"tests-{label}-{suffix}",
            Name = $"Tests {label} {suffix}",
            InitialEventId = 1,
            TaskAssignmentRoles = [ManagerRole],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    ExternalId = "TASK_REVIEW",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"],
                    RequiresClaim = requiresClaim,
                    AssigneeExpression = assignee
                },
                new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Complete", SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private static void MakeUnique(WorkflowModel model, string label)
    {
        var suffix = Guid.NewGuid().ToString("N");
        model.Id = $"tests-{label}-{suffix}";
        model.Name = $"Tests {label} {suffix}";
    }
}
