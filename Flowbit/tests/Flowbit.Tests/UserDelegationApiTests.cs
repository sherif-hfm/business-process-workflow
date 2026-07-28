using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class UserDelegationApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BatchCreationIsAtomicAndRejectsInvalidOrOverlappingGrantsCaseInsensitively()
    {
        var first = CreateModel("batch-first");
        var second = CreateModel("batch-second");
        await CreateWorkflowAsync(first);
        await CreateWorkflowAsync(second);
        var identitySuffix = Guid.NewGuid().ToString("N");
        var delegator = $"Alice{identitySuffix}";
        var delegateUser = $"Bob{identitySuffix}";

        var now = DateTimeOffset.UtcNow;
        var created = await CreateDelegationsAsync(
            delegator,
            delegateUser,
            [first.Id, second.Id],
            now.AddMinutes(-5),
            now.AddDays(2),
            "Annual leave");

        Assert.Equal(2, created.Count);
        Assert.Equal([first.Id, second.Id], created.Select(item => item.WorkflowKey).Order().ToArray());
        Assert.All(created, item =>
        {
            Assert.Equal(delegator, item.Delegator);
            Assert.Equal(delegateUser, item.Delegate);
            Assert.Equal("notRequired", item.AcceptanceState);
            Assert.False(item.RequiresAcceptance);
            Assert.True(item.IsActive);
            Assert.Equal("Annual leave", item.CreationReason);
        });

        using (var overlap = await SendAsync(
                   HttpMethod.Post,
                   "/api/user-delegations",
                   new CreateUserDelegationRequest(
                       delegateUser.ToUpperInvariant(),
                       [first.Id],
                       now,
                       now.AddDays(1)),
                   delegator.ToUpperInvariant()))
        {
            Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        }

        using (var self = await SendAsync(
                   HttpMethod.Post,
                   "/api/user-delegations",
                   new CreateUserDelegationRequest(
                       delegator.ToUpperInvariant(),
                       [first.Id],
                       now,
                       now.AddDays(1)),
                   delegator))
        {
            Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        }

        var atomicDelegator = $"atomic-{Guid.NewGuid():N}";
        using (var invalidBatch = await SendAsync(
                   HttpMethod.Post,
                   "/api/user-delegations",
                   new CreateUserDelegationRequest(
                       "delegate",
                       [first.Id, $"missing-{Guid.NewGuid():N}"],
                       now,
                       now.AddDays(1)),
                   atomicDelegator))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidBatch.StatusCode);
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            2,
            await db.UserDelegations.CountAsync(grant =>
                grant.Delegator == delegator.ToUpperInvariant()
                && grant.Delegate == delegateUser.ToLowerInvariant()));
        Assert.False(await db.UserDelegations.AnyAsync(grant =>
            grant.Delegator == atomicDelegator));

        var incoming = await ListAsync(delegateUser.ToUpperInvariant(), "incoming");
        Assert.Equal(2, incoming.TotalCount);
        Assert.All(incoming.Items, item => Assert.Equal(delegator, item.Delegator));
    }

    [Fact]
    public async Task AcceptancePolicyIsSnapshottedAndLifecycleUsesOptimisticConcurrency()
    {
        var model = CreateModel("acceptance");
        await CreateWorkflowAsync(model);
        var identitySuffix = Guid.NewGuid().ToString("N");
        var delegator = $"delegator{identitySuffix}";
        var delegateUser = $"delegate{identitySuffix}";
        var rejectingDelegate = $"rejecting{identitySuffix}";
        var newDelegate = $"new{identitySuffix}";

        using (var forbiddenPolicy = await SendAsync(
                   HttpMethod.Put,
                   $"/api/user-delegation-policies/{model.Id}",
                   new UpdateWorkflowDelegationPolicyRequest(true),
                   "ordinary",
                   ["Worker"],
                   suppressAdmin: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenPolicy.StatusCode);
        }

        var policy = await SetPolicyAsync(model.Id, true);
        Assert.True(policy.RequiresAcceptance);
        Assert.Equal("admin", policy.CreatedBy);

        var now = DateTimeOffset.UtcNow;
        var pending = Assert.Single(await CreateDelegationsAsync(
            delegator,
            delegateUser,
            [model.Id],
            now.AddMinutes(-1),
            now.AddDays(1)));
        Assert.True(pending.RequiresAcceptance);
        Assert.Equal("pending", pending.AcceptanceState);
        Assert.False(pending.IsActive);

        using (var wrongActor = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-delegations/{pending.Id}/accept",
                   new UserDelegationLifecycleRequest(pending.UpdatedAt),
                   "outsider"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongActor.StatusCode);
        }

        UserDelegationDto accepted;
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-delegations/{pending.Id}/accept",
                   new UserDelegationLifecycleRequest(pending.UpdatedAt, "I can cover"),
                   delegateUser.ToUpperInvariant()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            accepted = await ReadAsync<UserDelegationDto>(response);
        }
        Assert.Equal("accepted", accepted.AcceptanceState);
        Assert.True(accepted.IsActive);
        Assert.Equal(delegateUser.ToUpperInvariant(), accepted.DecisionBy);
        Assert.Equal("I can cover", accepted.DecisionReason);

        using (var stale = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-delegations/{pending.Id}/revoke",
                   new UserDelegationLifecycleRequest(pending.UpdatedAt),
                   delegator))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }

        UserDelegationDto revoked;
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-delegations/{accepted.Id}/revoke",
                   new UserDelegationLifecycleRequest(accepted.UpdatedAt, "Back early"),
                   delegator.ToUpperInvariant()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            revoked = await ReadAsync<UserDelegationDto>(response);
        }
        Assert.False(revoked.IsActive);
        Assert.Equal(delegator.ToUpperInvariant(), revoked.RevokedBy);
        Assert.Equal("Back early", revoked.RevocationReason);

        var retained = await ListAsync(delegator, "outgoing");
        Assert.Contains(retained.Items, item => item.Id == revoked.Id && item.RevokedAt is not null);

        var rejectedPending = Assert.Single(await CreateDelegationsAsync(
            delegator,
            rejectingDelegate,
            [model.Id],
            now,
            now.AddDays(1)));
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-delegations/{rejectedPending.Id}/reject",
                   new UserDelegationLifecycleRequest(rejectedPending.UpdatedAt, "Unavailable"),
                   rejectingDelegate.ToUpperInvariant()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rejected = await ReadAsync<UserDelegationDto>(response);
            Assert.Equal("rejected", rejected.AcceptanceState);
            Assert.False(rejected.IsActive);
            Assert.Equal("Unavailable", rejected.DecisionReason);
        }

        var updatedPolicy = await SetPolicyAsync(
            model.Id,
            false,
            policy.UpdatedAt);
        Assert.False(updatedPolicy.RequiresAcceptance);

        var snapshottedDefault = Assert.Single(await CreateDelegationsAsync(
            delegator,
            newDelegate,
            [model.Id],
            now,
            now.AddDays(1)));
        Assert.False(snapshottedDefault.RequiresAcceptance);
        Assert.Equal("notRequired", snapshottedDefault.AcceptanceState);
        Assert.Equal("pending", rejectedPending.AcceptanceState);
    }

    [Fact]
    public async Task SelfServiceAndAdministrationRequireAuthenticationAndConfiguredAuthority()
    {
        var model = CreateModel("authorization");
        await CreateWorkflowAsync(model);
        var now = DateTimeOffset.UtcNow;

        using (var anonymous = await fixture.Client.SendAsync(new HttpRequestMessage(
                   HttpMethod.Post,
                   "/api/user-delegations")
               {
                   Content = JsonContent.Create(
                       new CreateUserDelegationRequest(
                           "delegate",
                           [model.Id],
                           now,
                           now.AddHours(1)),
                       options: JsonOptions)
               }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var forbidden = await SendAsync(
                   HttpMethod.Get,
                   "/api/user-delegations/manage",
                   user: "ordinary",
                   roles: ["Worker"],
                   suppressAdmin: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using var create = await SendAsync(
            HttpMethod.Post,
            "/api/user-delegations/manage",
            new CreateManagedUserDelegationRequest(
                "represented",
                "delegate",
                [model.Id],
                now,
                now.AddHours(1),
                "Administrative coverage"),
            "administrator",
            ["admin"]);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var managed = Assert.Single(await ReadAsync<List<UserDelegationDto>>(create));
        Assert.Equal("represented", managed.Delegator);
        Assert.Equal("delegate", managed.Delegate);
        Assert.Equal("administrator", managed.CreatedBy);

        using var search = await SendAsync(
            HttpMethod.Get,
            $"/api/user-delegations/manage?delegator=REPRESENTED&delegate=DELEGATE&workflowKey={model.Id}",
            user: "administrator",
            roles: ["admin"]);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var page = await ReadAsync<PagedResult<UserDelegationDto>>(search);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(managed.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task DelegationWindowMustBeFiniteAndEndInTheFuture()
    {
        var model = CreateModel("validity");
        await CreateWorkflowAsync(model);
        var now = DateTimeOffset.UtcNow;

        foreach (var request in new[]
                 {
                     new CreateUserDelegationRequest(
                         "delegate",
                         [model.Id],
                         now,
                         now),
                     new CreateUserDelegationRequest(
                         "delegate",
                         [model.Id],
                         now.AddDays(-2),
                         now.AddDays(-1))
                 })
        {
            using var response = await SendAsync(
                HttpMethod.Post,
                "/api/user-delegations",
                request,
                $"delegator-{Guid.NewGuid():N}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private async Task<IReadOnlyList<UserDelegationDto>> CreateDelegationsAsync(
        string delegator,
        string delegateUser,
        IReadOnlyList<string> workflowKeys,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string? reason = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/user-delegations",
            new CreateUserDelegationRequest(
                delegateUser,
                workflowKeys,
                validFrom,
                validUntil,
                reason),
            delegator);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<List<UserDelegationDto>>(response);
    }

    private async Task<PagedResult<UserDelegationDto>> ListAsync(
        string user,
        string direction)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/user-delegations?direction={direction}&pageSize=200",
            user: user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<UserDelegationDto>>(response);
    }

    private async Task<WorkflowDelegationPolicyDto> SetPolicyAsync(
        string workflowKey,
        bool requiresAcceptance,
        DateTimeOffset? expectedUpdatedAt = null)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            $"/api/user-delegation-policies/{workflowKey}",
            new UpdateWorkflowDelegationPolicyRequest(
                requiresAcceptance,
                expectedUpdatedAt),
            "admin",
            ["admin"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<WorkflowDelegationPolicyDto>(response);
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true),
            "admin",
            ["admin"]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "admin",
        string[]? roles = null,
        bool suppressAdmin = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? []);
        if (suppressAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateModel(string label)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"delegation-{label}-{suffix}",
            Name = $"Delegation {label} {suffix}",
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
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"]
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
                    TargetRef = 3
                }
            ]
        };
    }
}
