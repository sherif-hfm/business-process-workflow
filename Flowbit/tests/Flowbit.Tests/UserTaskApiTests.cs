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
public sealed class UserTaskApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NormalLifecyclePersistsResolvedResultActorAndCorrelatedHistory()
    {
        var model = CreateModel("lifecycle", requiresClaim: true, condition: "amount > 5000");
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Reviewer"];
        model.FlowNodes.Insert(2, new FlowNodeModel
        {
            Id = 3,
            Name = "Follow up",
            Type = BpmnFlowNodeTypes.UserTask,
            Roles = ["Reviewer"]
        });
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsEnd(node.Type)).Id = 4;
        var action = model.SequenceFlows.Single(flow => flow.Id == 201);
        action.TargetRef = 3;
        action.Roles = ["Reviewer"];
        action.Variables =
        [
            new VariableModel
            {
                Id = 20,
                Name = "comment",
                DataType = WorkflowVariableTypes.String,
                Required = true
            }
        ];
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 301,
            Name = "Finish",
            SourceRef = 3,
            TargetRef = 4,
            Roles = ["Reviewer"]
        });

        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, 6000);
        var task = await GetSingleTaskAsync(instance.Id, "active", "finisher", "Reviewer");
        Assert.True(task.Capabilities.CanClaim);
        Assert.False(task.Capabilities.CanAct);

        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/claim",
                   user: "finisher",
                   roles: ["Reviewer"]))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            var claimed = await ReadAsync<UserTaskDto>(claim);
            Assert.True(claimed.Capabilities.ClaimedByMe);
            Assert.True(claimed.Capabilities.CanAct);
        }

        var submitted = new Dictionary<string, JsonElement>
        {
            ["comment"] = JsonSerializer.SerializeToElement("approved")
        };
        using var actionResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(submitted),
            "finisher",
            ["Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, actionResponse.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(actionResponse);
        Assert.Equal("running", ack.InstanceStatus);
        Assert.Equal(3, ack.CurrentNodeId);

        var completed = await GetTaskAsync(task.Id, "finisher", "Reviewer");
        Assert.Equal(UserTaskStatuses.Completed, completed.Status);
        Assert.Equal(201, completed.SelectedFlowId);
        Assert.Equal("finisher", completed.CompletedBy);
        Assert.Equal("approved", completed.Result!["comment"].GetString());
        Assert.False(completed.Capabilities.CanAct);
        Assert.NotNull(completed.CompletedAt);

        var next = await GetSingleTaskAsync(instance.Id, "active", "finisher", "Reviewer");
        Assert.NotEqual(task.Id, next.Id);
        Assert.Equal(3, next.NodeId);

        var detail = await GetInstanceAsync(instance.Id);
        var history = Assert.Single(detail.History, row => row.SequenceFlowId == 201);
        Assert.Equal(task.TokenId, history.TokenId);
        Assert.Equal(task.Id, history.UserTaskId);
        Assert.Equal("finisher", history.PerformedBy);
        Assert.Equal("approved", history.Payload!["comment"].GetString());

        await using var db = fixture.CreateDbContext();
        var stored = await db.UserTasks.SingleAsync(row => row.Id == task.Id);
        Assert.Equal(201, stored.SelectedFlowId);
        Assert.Equal("finisher", stored.CompletedBy);
        Assert.Equal("approved", stored.ResultJson!.RootElement.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task StaleTaskIdCannotAdvanceAReenteredSelfLoopTask()
    {
        var model = CreateModel("stale-loop", loop: true);
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, 1);
        var first = await GetSingleTaskAsync(instance.Id, "active", "worker");

        using (var firstAction = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{first.Id}/flows/201",
                   new TakeFlowRequest(null),
                   "worker"))
        {
            Assert.Equal(HttpStatusCode.OK, firstAction.StatusCode);
        }

        var second = await GetSingleTaskAsync(instance.Id, "active", "worker");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.NodeId, second.NodeId);
        Assert.Equal(first.TokenId, second.TokenId);

        using var replay = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{first.Id}/flows/201",
            new TakeFlowRequest(null),
            "worker");
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        var stillActive = await GetSingleTaskAsync(instance.Id, "active", "worker");
        Assert.Equal(second.Id, stillActive.Id);
        var completed = await ListTasksAsync(instance.Id, "completed", 1, 20, "worker");
        Assert.Single(completed.Items);
    }

    [Fact]
    public async Task BypassRolesStackWithTaskFlowConditionAndAnotherActorsClaim()
    {
        var model = CreateModel("bypass-roles", requiresClaim: true, condition: "amount > 5000");
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Reviewer"];
        var flow = model.SequenceFlows.Single(candidate => candidate.Id == 201);
        flow.Roles = ["Reviewer"];
        flow.CanActWithoutClaim = true;
        flow.CanActWithoutClaimRoles = ["Supervisor"];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, 7000);
        var task = await GetSingleTaskAsync(instance.Id, "active", "alice", "Reviewer");

        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/claim",
                   user: "alice",
                   roles: ["Reviewer"]))
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        var ownerFlows = await GetFlowsAsync(task.Id, "ALICE", "Reviewer");
        Assert.Single(ownerFlows);

        var ordinaryInbox = await GetInboxAsync(instance.Id, "bob", "Reviewer");
        var ordinaryItem = Assert.Single(ordinaryInbox.Items);
        Assert.False(ordinaryItem.CanAct);
        Assert.False(ordinaryItem.CanClaim);
        Assert.Empty(await GetFlowsAsync(task.Id, "bob", "Reviewer"));
        using (var forbidden = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/flows/201",
                   new TakeFlowRequest(null),
                   "bob",
                   ["Reviewer"]))
            Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);

        var supervisorInbox = await GetInboxAsync(instance.Id, "carol", "Reviewer", "supervisor");
        Assert.True(Assert.Single(supervisorInbox.Items).CanAct);
        Assert.Single(await GetFlowsAsync(task.Id, "carol", "Reviewer", "supervisor"));
        var supervisorTask = await GetTaskAsync(task.Id, "carol", "Reviewer", "supervisor");
        Assert.True(supervisorTask.Capabilities.CanAct);
        Assert.False(supervisorTask.Capabilities.ClaimedByMe);

        using var bypass = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "carol",
            ["Reviewer", "supervisor"]);
        Assert.Equal(HttpStatusCode.OK, bypass.StatusCode);
        Assert.Equal("carol", (await GetTaskAsync(task.Id, "carol", "Reviewer", "supervisor")).CompletedBy);
    }

    [Fact]
    public async Task EmptyBypassRolesPreserveExistingOtherwiseAuthorizedBehavior()
    {
        var model = CreateModel("empty-bypass", requiresClaim: true);
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Reviewer"];
        var flow = model.SequenceFlows.Single(candidate => candidate.Id == 201);
        flow.Roles = ["Reviewer"];
        flow.CanActWithoutClaim = true;
        flow.CanActWithoutClaimRoles = [];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, 1);
        var task = await GetSingleTaskAsync(instance.Id, "active", "alice", "Reviewer");

        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/claim",
                   user: "alice",
                   roles: ["Reviewer"]))
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        Assert.Single(await GetFlowsAsync(task.Id, "bob", "Reviewer"));
        using var action = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "bob",
            ["Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);
    }

    [Fact]
    public async Task ClaimBypassNeverOverridesDirectAssignment()
    {
        var model = CreateModel("assigned-bypass", requiresClaim: true);
        model.FlowNodes.Single(node => node.Id == 2).AssigneeExpression = "'alice'";
        var flow = model.SequenceFlows.Single(candidate => candidate.Id == 201);
        flow.CanActWithoutClaim = true;
        flow.CanActWithoutClaimRoles = [];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, 1);
        var task = await GetSingleTaskAsync(instance.Id, "active", "alice");
        Assert.Equal("alice", task.Assignee);
        Assert.False(task.RequiresClaim);

        Assert.Empty((await GetInboxAsync(instance.Id, "bob")).Items);
        Assert.Empty(await GetFlowsAsync(task.Id, "bob"));
        using (var forbidden = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/flows/201",
                   new TakeFlowRequest(null),
                   "bob"))
            Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);

        Assert.Single(await GetFlowsAsync(task.Id, "ALICE"));
        using var action = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "ALICE");
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);
    }

    [Fact]
    public async Task MultiInstanceActionsApplyTheSameBypassRoleRule()
    {
        var model = DefinitionValidationTests.LoadModel("votes-cardinality-approve-reject.json");
        var suffix = Guid.NewGuid().ToString("N");
        model.Id = "mi-bypass-" + suffix;
        model.Name = "MI bypass " + suffix;
        var node = model.FlowNodes.Single(candidate => candidate.Id == 2);
        node.RequiresClaim = true;
        node.MultiInstance!.OnePerActor = false;
        var approve = model.SequenceFlows.Single(candidate => candidate.Id == 201);
        approve.Roles = ["User"];
        approve.Condition = "voters > 0";
        approve.CanActWithoutClaim = true;
        approve.CanActWithoutClaimRoles = ["Supervisor"];
        var workflowId = await CreateWorkflowAsync(model);

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(
                workflowId,
                null,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["voters"] = JsonSerializer.SerializeToElement(1)
                }),
            "starter");
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var review = await ReadAsync<InstanceDetailDto>(startResponse);

        using (var enter = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{review.Id}/flows/204",
                   new TakeFlowRequest(null),
                   "manager",
                   ["Manager"]))
            Assert.Equal(HttpStatusCode.OK, enter.StatusCode);

        var task = await GetSingleTaskAsync(review.Id, "active", "alice", "User");
        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/claim",
                   user: "alice",
                   roles: ["User"]))
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        Assert.Empty(await GetFlowsAsync(task.Id, "bob", "User"));
        Assert.Single(await GetFlowsAsync(task.Id, "carol", "User", "Supervisor"));
        using var bypass = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "carol",
            ["User", "Supervisor"]);
        Assert.Equal(HttpStatusCode.OK, bypass.StatusCode);
        Assert.Equal("carol", (await GetTaskAsync(task.Id, "carol", "User", "Supervisor")).CompletedBy);
    }

    [Fact]
    public async Task StoredConditionControlsInboxClaimFlowAndPostAndSubmittedValueCannotUnlockIt()
    {
        var model = CreateModel("stored-condition", requiresClaim: true, condition: "amount > 5000");
        var flow = model.SequenceFlows.Single(candidate => candidate.Id == 201);
        flow.CanActWithoutClaim = true;
        flow.Variables =
        [
            new VariableModel
            {
                Id = 21,
                Name = "amount",
                DataType = WorkflowVariableTypes.Number,
                Required = true
            }
        ];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, 1000);
        var task = await GetSingleTaskAsync(instance.Id, "active", "worker");
        var originalUpdatedAt = task.UpdatedAt;

        var inbox = await GetInboxAsync(instance.Id, "worker");
        var item = Assert.Single(inbox.Items);
        Assert.False(item.CanClaim);
        Assert.False(item.CanAct);
        Assert.Empty(await GetFlowsAsync(task.Id, "worker"));

        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/claim",
                   user: "worker"))
            Assert.Equal(HttpStatusCode.BadRequest, claim.StatusCode);

        var submitted = new Dictionary<string, JsonElement>
        {
            ["amount"] = JsonSerializer.SerializeToElement(9000)
        };
        using (var direct = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/flows/201",
                   new TakeFlowRequest(submitted),
                   "worker"))
            Assert.Equal(HttpStatusCode.BadRequest, direct.StatusCode);

        var unchanged = await GetTaskAsync(task.Id, "worker");
        Assert.Equal(originalUpdatedAt, unchanged.UpdatedAt);
        Assert.Equal(UserTaskStatuses.Active, unchanged.Status);
        var detail = await GetInstanceAsync(instance.Id);
        Assert.DoesNotContain(detail.History, row => row.SequenceFlowId == 201);
        Assert.Equal(1000m, detail.Variables.Last(row => row.VariableName == "amount").Value.GetDecimal());
    }

    [Fact]
    public async Task ClaimRetriesAndUnclaimOverrideAreMutationSafeAndLegacyCompatible()
    {
        var model = CreateModel("unclaim", requiresClaim: true);
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Worker"];
        model.UnclaimRoles = ["Supervisor"];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, 1);
        var task = await GetSingleTaskAsync(instance.Id, "active", "alice", "Worker");

        UserTaskDto claimed;
        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/claim",
                   user: "alice",
                   roles: ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            claimed = await ReadAsync<UserTaskDto>(claim);
        }
        using (var retry = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/claim",
                   user: "ALICE",
                   roles: ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(claimed.UpdatedAt, (await ReadAsync<UserTaskDto>(retry)).UpdatedAt);
        }
        using (var competing = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/claim",
                   user: "bob",
                   roles: ["Worker"]))
            Assert.Equal(HttpStatusCode.Conflict, competing.StatusCode);

        var supervisorView = await GetTaskAsync(task.Id, "sue", "Supervisor");
        Assert.True(supervisorView.Capabilities.CanUnclaim);
        using (var unauthorized = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/unclaim",
                   user: "outsider"))
            Assert.Equal(HttpStatusCode.BadRequest, unauthorized.StatusCode);
        Assert.Equal(claimed.UpdatedAt, (await GetTaskAsync(task.Id, "alice", "Worker")).UpdatedAt);

        UserTaskDto released;
        using (var unclaim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/unclaim",
                   user: "sue",
                   roles: ["Supervisor"]))
        {
            Assert.Equal(HttpStatusCode.OK, unclaim.StatusCode);
            released = await ReadAsync<UserTaskDto>(unclaim);
            Assert.Null(released.ClaimedBy);
        }
        using (var retry = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/unclaim",
                   user: "sue",
                   roles: ["Supervisor"]))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(released.UpdatedAt, (await ReadAsync<UserTaskDto>(retry)).UpdatedAt);
        }
        using (var legacyRetry = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{instance.Id}/unclaim",
                   user: "sue",
                   roles: ["Supervisor"]))
            Assert.Equal(HttpStatusCode.OK, legacyRetry.StatusCode);
        Assert.Equal(released.UpdatedAt, (await GetTaskAsync(task.Id, "alice", "Worker")).UpdatedAt);
    }

    [Fact]
    public async Task ConcurrentNormalClaimsHaveExactlyOneWinner()
    {
        var workflowId = await CreateWorkflowAsync(CreateModel("concurrent-claim", requiresClaim: true));
        var instance = await StartAsync(workflowId, 1);
        var task = await GetSingleTaskAsync(instance.Id, "active", "alice");

        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, $"/api/user-tasks/{task.Id}/claim", user: "alice"),
            SendAsync(HttpMethod.Post, $"/api/user-tasks/{task.Id}/claim", user: "bob"));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        var winner = await GetTaskAsync(task.Id, "alice");
        Assert.Contains(winner.ClaimedBy, new[] { "alice", "bob" });
    }

    [Fact]
    public async Task ClaimedAndAssignedCancellationKeepOutcomeMetadataNull()
    {
        var model = CreateModel("cancelled-ownership", requiresClaim: true);
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Worker"];
        model.TaskAssignmentRoles = ["Manager"];
        var workflowId = await CreateWorkflowAsync(model);

        var claimedInstance = await StartAsync(workflowId, 1);
        var claimedTask = await GetSingleTaskAsync(claimedInstance.Id, "active", "alice", "Worker");
        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{claimedTask.Id}/claim",
                   user: "alice",
                   roles: ["Worker"]))
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using (var cancel = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{claimedInstance.Id}/cancel",
                   user: "alice",
                   roles: ["Worker"]))
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        AssertCancelledWithoutOutcome(
            await GetSingleTaskAsync(claimedInstance.Id, "cancelled", "alice", "Worker"));

        var assignedInstance = await StartAsync(workflowId, 2);
        var assignedTask = await GetSingleTaskAsync(assignedInstance.Id, "active", "manager", "Worker");
        using (var assign = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{assignedTask.Id}/assign",
                   new AssignUserTaskRequest("bob", assignedTask.UpdatedAt, "coverage"),
                   "manager",
                   ["Manager"]))
            Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        using (var cancel = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{assignedInstance.Id}/cancel",
                   user: "bob",
                   roles: ["Worker"]))
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        AssertCancelledWithoutOutcome(
            await GetSingleTaskAsync(assignedInstance.Id, "cancelled", "bob", "Worker"));
    }

    [Fact]
    public async Task TaskHistoryUsesAuthorizedStableDatabasePaging()
    {
        var model = CreateModel("paging", loop: true);
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Worker"];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId, 1);
        const int completionCount = 12;
        for (var index = 0; index < completionCount; index++)
        {
            var task = await GetSingleTaskAsync(instance.Id, "active", "worker", "Worker");
            using var action = await SendAsync(
                HttpMethod.Post,
                $"/api/user-tasks/{task.Id}/flows/201",
                new TakeFlowRequest(null),
                "worker",
                ["Worker"]);
            Assert.Equal(HttpStatusCode.OK, action.StatusCode);
        }

        var first = await ListTasksAsync(instance.Id, "completed", 1, 5, "worker", "Worker");
        var second = await ListTasksAsync(instance.Id, "completed", 2, 5, "worker", "Worker");
        Assert.Equal(completionCount, first.TotalCount);
        Assert.Equal(completionCount, second.TotalCount);
        Assert.Equal(5, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.Empty(first.Items.Select(task => task.Id).Intersect(second.Items.Select(task => task.Id)));
        Assert.True(first.Items[^1].UpdatedAt >= second.Items[0].UpdatedAt);

        var unauthorized = await ListTasksAsync(instance.Id, "completed", 1, 5, "outsider");
        Assert.Empty(unauthorized.Items);
        Assert.Equal(0, unauthorized.TotalCount);
    }

    [Fact]
    public async Task InboxProjectionUsesExactlyTwoQueriesAndPreservesPagingAndLatestVariables()
    {
        const int taskCount = 100;
        var model = CreateModel("inbox-query-budget", requiresClaim: true, condition: "amount > 5000");
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Worker"];
        model.SequenceFlows.Single(flow => flow.Id == 201).Roles = ["Worker"];
        var workflowId = await CreateWorkflowAsync(model);
        long[] expectedInstanceIds;

        await using (var db = fixture.CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            var instances = Enumerable.Range(0, taskCount)
                .Select(index => new WorkflowInstanceEntity
                {
                    WorkflowDefinitionId = workflowId,
                    WorkflowKey = model.Id,
                    Status = "running",
                    StartedBy = "starter",
                    CreatedAt = now.AddMilliseconds(index),
                    UpdatedAt = now.AddMilliseconds(index)
                })
                .ToList();
            db.WorkflowInstances.AddRange(instances);
            await db.SaveChangesAsync();

            var tokens = instances.Select(instance => new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 2,
                NodeName = "Review",
                NodeType = BpmnFlowNodeTypes.UserTask,
                Status = ExecutionTokenStatuses.Active,
                CreatedAt = instance.CreatedAt,
                UpdatedAt = instance.UpdatedAt
            }).ToList();
            db.ExecutionTokens.AddRange(tokens);
            await db.SaveChangesAsync();

            db.UserTasks.AddRange(instances.Select((instance, index) => new UserTaskEntity
            {
                InstanceId = instance.Id,
                TokenId = tokens[index].Id,
                NodeId = 2,
                NodeName = "Review",
                Roles = ["Worker"],
                RequiresClaim = true,
                Status = UserTaskStatuses.Active,
                CreatedAt = instance.CreatedAt,
                UpdatedAt = instance.UpdatedAt
            }));
            db.InstanceVariables.AddRange(instances.Select(instance => new InstanceVariableEntity
            {
                InstanceId = instance.Id,
                VariableName = "amount",
                ValueJson = JsonDocument.Parse("1000"),
                SetBy = "starter",
                SetAt = instance.CreatedAt
            }));
            db.InstanceVariables.Add(new InstanceVariableEntity
            {
                InstanceId = instances[^1].Id,
                VariableName = "amount",
                ValueJson = JsonDocument.Parse("6000"),
                SetBy = "latest-write",
                SetAt = now.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            expectedInstanceIds = instances
                .OrderByDescending(instance => instance.UpdatedAt)
                .ThenByDescending(instance => instance.Id)
                .Select(instance => instance.Id)
                .ToArray();
        }

        await GetInboxByWorkflowAsync(workflowId, 1, "worker", "Worker");

        PagedResult<InboxItemDto>? firstFifty = null;
        foreach (var pageSize in new[] { 1, 50, taskCount })
        {
            fixture.CommandCounter.Reset();
            var page = await GetInboxByWorkflowAsync(workflowId, pageSize, "worker", "Worker");

            Assert.Equal(2, fixture.CommandCounter.ReaderCommands);
            Assert.Equal(taskCount, page.TotalCount);
            Assert.Equal(pageSize, page.Items.Count);
            Assert.Equal(expectedInstanceIds.Take(pageSize).ToArray(),
                page.Items.Select(item => item.InstanceId).ToArray());
            firstFifty ??= pageSize == 50 ? page : null;
        }

        Assert.NotNull(firstFifty);
        Assert.True(firstFifty.Items[0].CanClaim);
        Assert.False(firstFifty.Items[0].CanAct);
        Assert.All(firstFifty.Items.Skip(1), item =>
        {
            Assert.False(item.CanAct);
            Assert.False(item.CanClaim);
        });

        fixture.CommandCounter.Reset();
        var secondFifty = await GetInboxPageByWorkflowAsync(workflowId, 2, 50, "worker", "Worker");
        Assert.Equal(2, fixture.CommandCounter.ReaderCommands);
        Assert.Equal(taskCount, secondFifty.TotalCount);
        Assert.Equal(expectedInstanceIds.Skip(50).ToArray(),
            secondFifty.Items.Select(item => item.InstanceId).ToArray());
        Assert.Empty(firstFifty.Items.Select(item => item.UserTaskId)
            .Intersect(secondFifty.Items.Select(item => item.UserTaskId)));

        fixture.CommandCounter.Reset();
        var outOfRange = await GetInboxPageByWorkflowAsync(workflowId, 3, 50, "worker", "Worker");
        Assert.Equal(2, fixture.CommandCounter.ReaderCommands);
        Assert.Equal(taskCount, outOfRange.TotalCount);
        Assert.Empty(outOfRange.Items);

        fixture.CommandCounter.Reset();
        var empty = await GetInboxAsync(long.MaxValue, "worker", "Worker");
        Assert.Equal(1, fixture.CommandCounter.ReaderCommands);
        Assert.Equal(0, empty.TotalCount);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public async Task MultiInstanceInboxProjectionUsesExactlyTwoQueriesAndReturnsCorrectProgress()
    {
        const int totalCount = 101;
        const int activeCount = totalCount - 1;
        var model = DefinitionValidationTests.LoadModel("votes-cardinality-approve-reject.json");
        var suffix = Guid.NewGuid().ToString("N");
        model.Id = $"mi-inbox-query-budget-{suffix}";
        model.Name = $"MI inbox query budget {suffix}";
        model.FlowNodes.Single(node => node.Id == 2).MultiInstance!.OnePerActor = false;
        var workflowId = await CreateWorkflowAsync(model);

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(
                workflowId,
                null,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["voters"] = JsonSerializer.SerializeToElement(totalCount)
                }),
            "manager",
            ["Manager"]);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var review = await ReadAsync<InstanceDetailDto>(startResponse);

        using var enterResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{review.Id}/flows/204",
            new TakeFlowRequest(null),
            "manager",
            ["Manager"]);
        Assert.Equal(HttpStatusCode.OK, enterResponse.StatusCode);

        var warm = await GetInboxByWorkflowAsync(workflowId, totalCount, "worker", "User");
        Assert.Equal(totalCount, warm.TotalCount);
        var completedTaskId = warm.Items[0].UserTaskId;
        using var complete = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{completedTaskId}/flows/201",
            new TakeFlowRequest(null),
            "worker",
            ["User"]);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        foreach (var pageSize in new[] { 1, 50, activeCount })
        {
            fixture.CommandCounter.Reset();
            var page = await GetInboxByWorkflowAsync(workflowId, pageSize, "worker", "User");

            Assert.Equal(2, fixture.CommandCounter.ReaderCommands);
            Assert.Equal(activeCount, page.TotalCount);
            Assert.Equal(pageSize, page.Items.Count);
            Assert.Equal(pageSize, page.Items.Select(item => item.UserTaskId).Distinct().Count());
            Assert.DoesNotContain(page.Items, item => item.UserTaskId == completedTaskId);
            Assert.All(page.Items, item =>
            {
                Assert.True(item.CanAct);
                Assert.Equal(item.MultiInstanceExecutionId, item.MultiInstance?.ExecutionId);
                var progress = Assert.IsType<MultiInstanceProgressDto>(item.MultiInstance);
                Assert.Equal(totalCount, progress.Total);
                Assert.Equal(1, progress.Completed);
                Assert.Equal(activeCount, progress.Active);
                Assert.Equal(0, progress.Pending);
                Assert.Equal(0, progress.Cancelled);
                Assert.Equal(1, Assert.Single(progress.FlowCounts, count => count.FlowId == 201).Count);
            });
        }
    }

    [Fact]
    public async Task InstanceUserTaskQueryCountIsPageBoundedForOnePerActorExecution()
    {
        const int taskCount = 100;
        var model = DefinitionValidationTests.LoadModel("votes-cardinality-approve-reject.json");
        var suffix = Guid.NewGuid().ToString("N");
        model.Id = $"task-page-query-budget-{suffix}";
        model.Name = $"Task page query budget {suffix}";
        var workflowId = await CreateWorkflowAsync(model);

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(
                workflowId,
                null,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["voters"] = JsonSerializer.SerializeToElement(taskCount)
                }),
            "manager",
            ["Manager"]);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var review = await ReadAsync<InstanceDetailDto>(startResponse);

        using var enterResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{review.Id}/flows/204",
            new TakeFlowRequest(null),
            "manager",
            ["Manager"]);
        Assert.Equal(HttpStatusCode.OK, enterResponse.StatusCode);

        await ListTasksAsync(review.Id, "active", 1, 1, "worker", "User");

        fixture.CommandCounter.Reset();
        var one = await ListTasksAsync(review.Id, "active", 1, 1, "worker", "User");
        var oneItemCommands = fixture.CommandCounter.ReaderCommands;

        fixture.CommandCounter.Reset();
        var hundred = await ListTasksAsync(review.Id, "active", 1, taskCount, "worker", "User");
        var hundredItemCommands = fixture.CommandCounter.ReaderCommands;

        Assert.Equal(taskCount, one.TotalCount);
        Assert.Single(one.Items);
        Assert.Equal(taskCount, hundred.TotalCount);
        Assert.Equal(taskCount, hundred.Items.Count);
        Assert.All(hundred.Items, task => Assert.True(task.Capabilities.CanAct));
        Assert.Equal(oneItemCommands, hundredItemCommands);
        Assert.InRange(hundredItemCommands, 1, 12);

        using var complete = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{hundred.Items[0].Id}/flows/201",
            new TakeFlowRequest(null),
            "worker",
            ["User"]);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var afterCompletion = await ListTasksAsync(
            review.Id, "active", 1, taskCount, "worker", "User");
        Assert.Equal(taskCount - 1, afterCompletion.TotalCount);
        Assert.All(afterCompletion.Items, task =>
        {
            Assert.False(task.Capabilities.CanAct);
            Assert.False(task.Capabilities.CanClaim);
        });
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

    private async Task<InstanceDetailDto> StartAsync(long workflowId, decimal amount)
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
                    ["amount"] = JsonSerializer.SerializeToElement(amount)
                }),
            "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<UserTaskDto> GetSingleTaskAsync(
        long instanceId,
        string status,
        string user,
        params string[] roles) =>
        Assert.Single((await ListTasksAsync(instanceId, status, 1, 200, user, roles)).Items);

    private async Task<PagedResult<UserTaskDto>> ListTasksAsync(
        long instanceId,
        string status,
        int page,
        int pageSize,
        string user,
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status={status}&page={page}&pageSize={pageSize}",
            user: user,
            roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<UserTaskDto>>(response);
    }

    private async Task<UserTaskDto> GetTaskAsync(long taskId, string user, params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/user-tasks/{taskId}",
            user: user,
            roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UserTaskDto>(response);
    }

    private async Task<IReadOnlyList<SequenceFlowModel>> GetFlowsAsync(
        long taskId,
        string user,
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/user-tasks/{taskId}/flows",
            user: user,
            roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<List<SequenceFlowModel>>(response);
    }

    private async Task<PagedResult<InboxItemDto>> GetInboxAsync(
        long instanceId,
        string user,
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/inbox?instanceId={instanceId}&pageSize=200",
            user: user,
            roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<InboxItemDto>>(response);
    }

    private async Task<PagedResult<InboxItemDto>> GetInboxByWorkflowAsync(
        long workflowId,
        int pageSize,
        string user,
        params string[] roles) =>
        await GetInboxPageByWorkflowAsync(workflowId, 1, pageSize, user, roles);

    private async Task<PagedResult<InboxItemDto>> GetInboxPageByWorkflowAsync(
        long workflowId,
        int page,
        int pageSize,
        string user,
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/inbox?workflowId={workflowId}&page={page}&pageSize={pageSize}",
            user: user,
            roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<InboxItemDto>>(response);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, user, roles ?? []);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static void AssertCancelledWithoutOutcome(UserTaskDto task)
    {
        Assert.Equal(UserTaskStatuses.Cancelled, task.Status);
        Assert.Null(task.SelectedFlowId);
        Assert.Null(task.CompletedBy);
        Assert.Null(task.Result);
        Assert.NotNull(task.CompletedAt);
        Assert.False(task.Capabilities.CanAct);
    }

    private static WorkflowModel CreateModel(
        string label,
        bool requiresClaim = false,
        string? condition = null,
        bool loop = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = new WorkflowModel
        {
            Id = $"user-task-{label}-{suffix}",
            Name = $"User task {label} {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables =
                    [
                        new VariableModel
                        {
                            Id = 10,
                            Name = "amount",
                            DataType = WorkflowVariableTypes.Number,
                            Required = true
                        }
                    ]
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    RequiresClaim = requiresClaim
                },
                new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Review", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Continue",
                    SourceRef = 2,
                    TargetRef = loop ? 2 : 3,
                    Condition = condition
                }
            ]
        };
        if (loop)
        {
            model.SequenceFlows.Add(new SequenceFlowModel
            {
                Id = 202,
                Name = "Finish",
                SourceRef = 2,
                TargetRef = 3
            });
        }

        return model;
    }
}
