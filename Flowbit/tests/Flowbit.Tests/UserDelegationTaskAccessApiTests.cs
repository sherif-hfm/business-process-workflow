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
public sealed class UserDelegationTaskAccessApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task DelegateCanActOnExistingAndFutureAssignedTasksWithDualAuditAttribution()
    {
        var owner = $"owner{Guid.NewGuid():N}";
        var delegateUser = $"delegate{Guid.NewGuid():N}";
        var model = CreateAssignedAuditModel(owner, delegateUser);
        var workflowId = await CreateWorkflowAsync(model);

        var existing = await StartAsync(workflowId);
        var ownerBeforeGrant = Assert.Single(
            (await GetInboxAsync(existing.Id, owner, "Worker")).Items);
        Assert.Null(ownerBeforeGrant.DelegatedAccess);
        Assert.True(ownerBeforeGrant.CanAct);
        Assert.Empty((await GetInboxAsync(existing.Id, delegateUser, "Worker")).Items);

        var delegation = await CreateDelegationAsync(owner, delegateUser, model.Id);

        var delegatedExisting = Assert.Single(
            (await GetInboxAsync(existing.Id, delegateUser, "Worker")).Items);
        Assert.Equal(delegation.Id, delegatedExisting.DelegatedAccess?.DelegationId);
        Assert.Equal(owner, delegatedExisting.DelegatedAccess?.ActingFor);
        Assert.False(delegatedExisting.ClaimedByMe);
        Assert.False(delegatedExisting.CanClaim);
        Assert.True(delegatedExisting.CanAct);

        var ownerAfterGrant = Assert.Single(
            (await GetInboxAsync(existing.Id, owner, "Worker")).Items);
        Assert.Null(ownerAfterGrant.DelegatedAccess);
        Assert.True(ownerAfterGrant.CanAct);

        var future = await StartAsync(workflowId);
        var futureDelegated = Assert.Single(
            (await GetInboxAsync(future.Id, delegateUser, "Worker")).Items);
        Assert.Equal(delegation.Id, futureDelegated.DelegatedAccess?.DelegationId);
        Assert.Equal(owner, futureDelegated.DelegatedAccess?.ActingFor);

        // Warm settings/caches before measuring the authoritative count + page query budget.
        await GetInboxAsync(future.Id, delegateUser, "Worker");
        fixture.CommandCounter.Reset();
        var measured = await GetInboxAsync(future.Id, delegateUser, "Worker");
        Assert.Equal(2, fixture.CommandCounter.ReaderCommands);
        Assert.Equal(1, measured.TotalCount);

        var detail = await GetTaskAsync(
            delegatedExisting.UserTaskId,
            delegateUser,
            "Worker");
        Assert.Equal(delegation.Id, detail.DelegatedAccess?.DelegationId);
        Assert.Equal(owner, detail.DelegatedAccess?.ActingFor);
        Assert.False(detail.Capabilities.ClaimedByMe);
        Assert.True(detail.Capabilities.CanAct);

        var flows = await GetFlowsAsync(
            delegatedExisting.UserTaskId,
            delegateUser,
            "Worker");
        Assert.Equal(201, Assert.Single(flows).Id);

        var values = new Dictionary<string, JsonElement>
        {
            ["comment"] = JsonSerializer.SerializeToElement("covered by delegate")
        };
        UserTaskActionAckDto action;
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{delegatedExisting.UserTaskId}/flows/201",
                   new TakeFlowRequest(values),
                   delegateUser,
                   ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            action = await ReadAsync<UserTaskActionAckDto>(response);
        }
        Assert.Equal(delegation.Id, action.DelegatedAccess?.DelegationId);
        Assert.Equal(owner, action.DelegatedAccess?.ActingFor);
        Assert.Equal(WorkflowInstanceStatuses.Completed, action.InstanceStatus);

        await using var db = fixture.CreateDbContext();
        var storedTask = await db.UserTasks.SingleAsync(
            task => task.Id == delegatedExisting.UserTaskId);
        Assert.Equal(UserTaskStatuses.Completed, storedTask.Status);
        Assert.Equal(delegateUser, storedTask.CompletedBy);
        Assert.Equal(owner, storedTask.CompletedActingFor);
        Assert.Equal(delegation.Id, storedTask.CompletionDelegationId);

        var actionHistory = await db.InstanceHistory.SingleAsync(row =>
            row.InstanceId == existing.Id
            && row.ActionId == 201
            && row.UserTaskId == delegatedExisting.UserTaskId);
        Assert.Equal(delegateUser, actionHistory.PerformedBy);
        Assert.Equal(owner, actionHistory.ActingFor);
        Assert.Equal(delegation.Id, actionHistory.DelegationId);

        var submittedVariable = await db.InstanceVariables.SingleAsync(row =>
            row.InstanceId == existing.Id
            && row.VariableName == "comment");
        Assert.Equal(delegateUser, submittedVariable.SetBy);
        Assert.Equal(owner, submittedVariable.ActingFor);
        Assert.Equal(delegation.Id, submittedVariable.DelegationId);

        var taskExecution = await db.NodeExecutions.SingleAsync(row =>
            row.InstanceId == existing.Id
            && row.NodeId == 2
            && row.UserTaskId == delegatedExisting.UserTaskId);
        Assert.Equal(delegateUser, taskExecution.CompletedBy);
        Assert.Equal(owner, taskExecution.CompletedActingFor);
        Assert.Equal(delegation.Id, taskExecution.CompletedDelegationId);

        var occurrence = await db.SequenceFlowOccurrences.SingleAsync(row =>
            row.InstanceId == existing.Id
            && row.SequenceFlowId == 201
            && row.IsAction);
        Assert.True(occurrence.IsTraversal);
        Assert.Equal(delegateUser, occurrence.User);
        Assert.Equal(owner, occurrence.ActingFor);
        Assert.Equal(delegation.Id, occurrence.DelegationId);
        Assert.Contains("Worker", occurrence.UserRoles);

        var summary = await db.SequenceFlowSummaries.SingleAsync(row =>
            row.InstanceId == existing.Id
            && row.SequenceFlowId == 201);
        Assert.Equal(delegateUser, summary.LastActionUser);
        Assert.Equal(owner, summary.LastActionActingFor);
        Assert.Equal(delegation.Id, summary.LastActionDelegationId);
        Assert.Equal(delegateUser, summary.LastTraversalUser);
        Assert.Equal(owner, summary.LastTraversalActingFor);
        Assert.Equal(delegation.Id, summary.LastTraversalDelegationId);

        var instance = await GetInstanceAsync(existing.Id);
        var observedActingFor = instance.Variables
            .Where(variable => variable.VariableName == "observedActingFor")
            .OrderByDescending(variable => variable.Id)
            .First();
        Assert.Equal(owner, observedActingFor.Value.GetString());
        Assert.Equal(delegateUser, observedActingFor.SetBy);
        Assert.Equal(owner, observedActingFor.ActingFor);
        Assert.Equal(delegation.Id, observedActingFor.DelegationId);
        var observedDelegationId = instance.Variables
            .Where(variable => variable.VariableName == "observedDelegationId")
            .OrderByDescending(variable => variable.Id)
            .First();
        Assert.Equal(delegation.Id, observedDelegationId.Value.GetInt64());
        Assert.Equal(delegateUser, observedDelegationId.SetBy);
        Assert.Equal(owner, observedDelegationId.ActingFor);
        Assert.Equal(delegation.Id, observedDelegationId.DelegationId);
    }

    [Fact]
    public async Task DelegateCanUnclaimForOwnerButSubsequentPoolClaimIsDirectAndDelegationIsNotTransitive()
    {
        var owner = $"claimant{Guid.NewGuid():N}";
        var delegateUser = $"delegate{Guid.NewGuid():N}";
        var transitiveUser = $"transitive{Guid.NewGuid():N}";
        var model = CreateClaimModel();
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId);
        var poolTask = Assert.Single(
            (await GetInboxAsync(instance.Id, owner, "Worker")).Items);

        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{poolTask.UserTaskId}/claim",
                   user: owner,
                   roles: ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }

        var ownerGrant = await CreateDelegationAsync(owner, delegateUser, model.Id);
        await CreateDelegationAsync(delegateUser, transitiveUser, model.Id);

        var delegatedInbox = Assert.Single(
            (await GetInboxAsync(instance.Id, delegateUser, "Worker")).Items);
        Assert.Equal(owner, delegatedInbox.ClaimedBy);
        Assert.False(delegatedInbox.ClaimedByMe);
        Assert.True(delegatedInbox.CanAct);
        Assert.Equal(ownerGrant.Id, delegatedInbox.DelegatedAccess?.DelegationId);
        Assert.Empty((await GetInboxAsync(instance.Id, transitiveUser, "Worker")).Items);
        Assert.Empty(await GetFlowsAsync(
            poolTask.UserTaskId,
            transitiveUser,
            "Worker"));

        var delegatedDetail = await GetTaskAsync(
            poolTask.UserTaskId,
            delegateUser,
            "Worker");
        Assert.False(delegatedDetail.Capabilities.ClaimedByMe);
        Assert.False(delegatedDetail.Capabilities.CanClaim);
        Assert.True(delegatedDetail.Capabilities.CanUnclaim);
        Assert.True(delegatedDetail.Capabilities.CanAct);
        Assert.Equal(ownerGrant.Id, delegatedDetail.DelegatedAccess?.DelegationId);

        UserTaskDto unclaimed;
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{poolTask.UserTaskId}/unclaim",
                   user: delegateUser,
                   roles: ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            unclaimed = await ReadAsync<UserTaskDto>(response);
        }
        Assert.Null(unclaimed.ClaimedBy);
        Assert.Equal(ownerGrant.Id, unclaimed.DelegatedAccess?.DelegationId);
        Assert.Equal(owner, unclaimed.DelegatedAccess?.ActingFor);

        var afterUnclaim = await GetTaskAsync(
            poolTask.UserTaskId,
            delegateUser,
            "Worker");
        Assert.Null(afterUnclaim.DelegatedAccess);
        Assert.True(afterUnclaim.Capabilities.CanClaim);

        using (var claimAsSelf = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{poolTask.UserTaskId}/claim",
                   user: delegateUser,
                   roles: ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, claimAsSelf.StatusCode);
            var directlyClaimed = await ReadAsync<UserTaskDto>(claimAsSelf);
            Assert.Equal(delegateUser, directlyClaimed.ClaimedBy);
            Assert.True(directlyClaimed.Capabilities.ClaimedByMe);
            Assert.Null(directlyClaimed.DelegatedAccess);
        }

        await using var db = fixture.CreateDbContext();
        var audit = await db.InstanceHistory.SingleAsync(row =>
            row.InstanceId == instance.Id
            && row.UserTaskId == poolTask.UserTaskId
            && row.Note == "taskClaim");
        Assert.Equal(delegateUser, audit.PerformedBy);
        Assert.Equal(owner, audit.ActingFor);
        Assert.Equal(ownerGrant.Id, audit.DelegationId);
        Assert.Equal(
            "unclaimed",
            audit.Payload!.RootElement.GetProperty("operation").GetString());
        Assert.Equal(
            owner,
            audit.Payload.RootElement.GetProperty("previousClaimedBy").GetString());
    }

    private async Task<UserDelegationDto> CreateDelegationAsync(
        string owner,
        string delegateUser,
        string workflowKey)
    {
        var now = DateTimeOffset.UtcNow;
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/user-delegations",
            new CreateUserDelegationRequest(
                delegateUser,
                [workflowKey],
                now.AddMinutes(-1),
                now.AddDays(1)),
            owner);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.Single(await ReadAsync<List<UserDelegationDto>>(response));
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
            new StartInstanceRequest(workflowId, null, null, null),
            "starter",
            ["Worker"]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
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

    private async Task<UserTaskDto> GetTaskAsync(
        long taskId,
        string user,
        params string[] roles)
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

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "admin",
        string[]? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? []);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateAssignedAuditModel(
        string owner,
        string delegateUser)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"delegated-assigned-{suffix}",
            Name = $"Delegated assigned {suffix}",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "observedActingFor",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement(string.Empty)
                },
                new VariableModel
                {
                    Id = 2,
                    Name = "observedDelegationId",
                    DataType = WorkflowVariableTypes.Number,
                    DefaultValue = JsonSerializer.SerializeToElement(0)
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
                    Name = "Assigned review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"],
                    AssigneeExpression = $"'{owner}'"
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Read delegation evidence",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.JavaScript,
                    UsesFlowInfo = true,
                    Script =
                        "const info = execution.getFlowInfo(201); " +
                        "execution.setVariable('observedActingFor', info.actions.last.actingFor); " +
                        "execution.setVariable('observedDelegationId', info.actions.last.delegationId);"
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Done",
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
                    Name = "Complete",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Worker"],
                    Condition =
                        $"([sys.user] == '{delegateUser}' and [sys.actingFor] == '{owner}') " +
                        $"or ([sys.user] == '{owner}' and IsNullOrEmpty([sys.actingFor]))",
                    Variables =
                    [
                        new VariableModel
                        {
                            Id = 10,
                            Name = "comment",
                            DataType = WorkflowVariableTypes.String,
                            Required = true
                        }
                    ]
                },
                new SequenceFlowModel
                {
                    Id = 301,
                    SourceRef = 3,
                    TargetRef = 4
                }
            ]
        };
    }

    private static WorkflowModel CreateClaimModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"delegated-claim-{suffix}",
            Name = $"Delegated claim {suffix}",
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
                    Name = "Claimed review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"],
                    RequiresClaim = true
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
                    Id = 101,
                    SourceRef = 1,
                    TargetRef = 2
                },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Complete",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Worker"]
                }
            ]
        };
    }
}
