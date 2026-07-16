using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Flowbit.Api.Auth;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class ActorContextResolverTests
{
    [Fact]
    public void Legacy_configuration_uses_identity_name_and_preserves_roles_and_claims()
    {
        var resolver = CreateResolver();
        var principal = Principal(
            new Claim(ClaimTypes.Name, "legacy-user"),
            new Claim(ClaimTypes.NameIdentifier, "fallback-user"),
            new Claim(ClaimTypes.Role, "Manager"),
            new Claim("department", "Finance"));

        var actor = resolver.Resolve(principal);

        Assert.Equal("legacy-user", actor.User);
        Assert.Equal(["Manager"], actor.Roles);
        Assert.Equal("Finance", actor.Claims["department"]);
    }

    [Fact]
    public void Legacy_configuration_falls_back_to_name_identifier()
    {
        var resolver = CreateResolver();
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, "subject-user"));

        Assert.Equal("subject-user", resolver.Resolve(principal).User);
    }

    [Fact]
    public void Configured_claim_overrides_legacy_name_and_is_trimmed()
    {
        var resolver = CreateResolver("preferred_username");
        var principal = Principal(
            new Claim(ClaimTypes.Name, "legacy-user"),
            new Claim("preferred_username", "  canonical-user  "));

        Assert.Equal("canonical-user", resolver.Resolve(principal).User);
    }

    [Fact]
    public void Configured_short_sub_claim_accepts_framework_mapped_name_identifier()
    {
        var resolver = CreateResolver("sub");
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, "subject-user"));

        Assert.Equal("subject-user", resolver.Resolve(principal).User);
    }

    [Fact]
    public void Configured_mapped_claim_accepts_short_sub_claim()
    {
        var resolver = CreateResolver(ClaimTypes.NameIdentifier);
        var principal = Principal(new Claim("sub", "subject-user"));

        Assert.Equal("subject-user", resolver.Resolve(principal).User);
    }

    [Fact]
    public void Duplicate_case_equivalent_values_resolve_to_one_engine_identity()
    {
        var resolver = CreateResolver("preferred_username");
        var principal = Principal(
            new Claim("preferred_username", "Alice"),
            new Claim("preferred_username", "alice"));

        Assert.Equal("Alice", resolver.Resolve(principal).User);
    }

    [Fact]
    public void Missing_configured_claim_fails_closed()
    {
        var resolver = CreateResolver("preferred_username");

        Assert.Throws<WorkflowUnauthorizedException>(() =>
            resolver.Resolve(Principal(new Claim(ClaimTypes.Name, "legacy-user"))));
    }

    [Fact]
    public void Blank_configured_claim_value_fails_closed()
    {
        var resolver = CreateResolver("preferred_username");

        Assert.Throws<WorkflowUnauthorizedException>(() =>
            resolver.Resolve(Principal(new Claim("preferred_username", "  "))));
    }

    [Fact]
    public void Ambiguous_configured_claim_values_fail_closed()
    {
        var resolver = CreateResolver("preferred_username");
        var principal = Principal(
            new Claim("preferred_username", "alice"),
            new Claim("preferred_username", "bob"));

        Assert.Throws<WorkflowUnauthorizedException>(() => resolver.Resolve(principal));
    }

    [Fact]
    public void Oversized_configured_claim_value_fails_closed()
    {
        var resolver = CreateResolver("preferred_username");
        var principal = Principal(
            new Claim("preferred_username", new string('a', UserTaskConstraints.MaxActorNameLength + 1)));

        Assert.Throws<WorkflowUnauthorizedException>(() => resolver.Resolve(principal));
    }

    [Fact]
    public void Configured_claim_accepts_the_300_character_actor_limit()
    {
        var resolver = CreateResolver("preferred_username");
        var expected = new string('a', UserTaskConstraints.MaxActorNameLength);

        Assert.Equal(expected, resolver.Resolve(
            Principal(new Claim("preferred_username", expected))).User);
    }

    [Fact]
    public void Present_blank_engine_setting_is_invalid()
    {
        var configuration = new ActorIdentityConfiguration();

        Assert.Throws<InvalidOperationException>(() => configuration.Initialize(Setting("  ")));
    }

    private static ActorContextResolver CreateResolver(string? claimType = null)
    {
        var configuration = new ActorIdentityConfiguration();
        configuration.Initialize(claimType is null ? null : Setting(claimType));
        return new ActorContextResolver(configuration);
    }

    private static EngineSettingRecord Setting(string value) =>
        new(1, "Authentication", "UserIdentityClaim", value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));
}

[Collection(PostgresApiCollection.Name)]
public sealed class ActorContextApiTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task Auth_context_returns_the_server_resolved_legacy_actor()
    {
        using var request = ApiTestAuth.Authorize(
            new HttpRequestMessage(HttpMethod.Get, "/api/auth/context"),
            "actor-context-user",
            "Reviewer");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var context = await response.Content.ReadFromJsonAsync<ActorContextDto>();
        Assert.NotNull(context);
        Assert.Equal("actor-context-user", context.User);
        Assert.Contains("Reviewer", context.Roles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Engine_setting_takes_effect_only_for_a_new_api_process()
    {
        await using var db = fixture.CreateDbContext();
        var setting = new EngineSettingEntity
        {
            Namespace = "Authentication",
            Key = "UserIdentityClaim",
            Value = "preferred_username"
        };
        db.EngineSettings.Add(setting);
        await db.SaveChangesAsync();

        try
        {
            // The fixture API was already started without the row and must retain
            // its process-latched legacy identity.
            using var existingRequest = ApiTestAuth.AuthorizeWithClaims(
                new HttpRequestMessage(HttpMethod.Get, "/api/auth/context"),
                "legacy-user",
                [new Claim("preferred_username", "canonical-user")],
                "Reviewer");
            using var existingResponse = await fixture.Client.SendAsync(existingRequest);
            var existingContext = await existingResponse.Content.ReadFromJsonAsync<ActorContextDto>();
            Assert.Equal("legacy-user", existingContext?.User);

            await using var factory = new IdentityApiFactory(fixture.ConnectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            using var configuredRequest = ApiTestAuth.AuthorizeWithClaims(
                new HttpRequestMessage(HttpMethod.Get, "/api/auth/context"),
                "legacy-user",
                [new Claim("preferred_username", "canonical-user")],
                "Reviewer");
            using var configuredResponse = await client.SendAsync(configuredRequest);
            Assert.Equal(HttpStatusCode.OK, configuredResponse.StatusCode);
            var configuredContext = await configuredResponse.Content.ReadFromJsonAsync<ActorContextDto>();
            Assert.Equal("canonical-user", configuredContext?.User);

            using var missingClaimRequest = ApiTestAuth.Authorize(
                new HttpRequestMessage(HttpMethod.Get, "/api/auth/context"),
                "legacy-user",
                "Reviewer");
            using var missingClaimResponse = await client.SendAsync(missingClaimRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, missingClaimResponse.StatusCode);

            var suffix = Guid.NewGuid().ToString("N");
            var model = new WorkflowModel
            {
                Id = "actor-identity-" + suffix,
                Name = "Actor identity " + suffix,
                InitialEventId = 1,
                Variables =
                [
                    new VariableModel
                    {
                        Id = 1,
                        Name = "capturedUser",
                        DataType = WorkflowVariableTypes.String,
                        DefaultValue = JsonSerializer.SerializeToElement("${sys.user}")
                    }
                ],
                FlowNodes =
                [
                    new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                    new FlowNodeModel
                    {
                        Id = 2,
                        Name = "Assigned",
                        Type = BpmnFlowNodeTypes.UserTask,
                        AssigneeExpression = "[sys.user]"
                    },
                    new FlowNodeModel
                    {
                        Id = 3,
                        Name = "Claimed",
                        Type = BpmnFlowNodeTypes.UserTask,
                        RequiresClaim = true
                    },
                    new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
                ],
                SequenceFlows =
                [
                    new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                    new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                    new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
                ]
            };

            using var createRequest = ConfiguredRequest(
                HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
            using var createResponse = await client.SendAsync(createRequest);
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            var workflow = await createResponse.Content.ReadFromJsonAsync<WorkflowDetailDto>();
            Assert.NotNull(workflow);

            using var startRequest = ConfiguredRequest(
                HttpMethod.Post,
                "/api/instances",
                new StartInstanceRequest(workflow.Id, null, null, null));
            using var startResponse = await client.SendAsync(startRequest);
            Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
            var started = await startResponse.Content.ReadFromJsonAsync<StartInstanceResultDto>();
            Assert.NotNull(started);
            Assert.Equal("canonical-user", started.StartedBy);

            using var inboxRequest = ConfiguredRequest(
                HttpMethod.Get, $"/api/instances/inbox?instanceId={started.Id}");
            using var inboxResponse = await client.SendAsync(inboxRequest);
            var inbox = await inboxResponse.Content.ReadFromJsonAsync<PagedResult<InboxItemDto>>();
            var assignedTask = Assert.Single(inbox!.Items);
            Assert.Equal("canonical-user", assignedTask.Assignee);
            Assert.True(assignedTask.CanAct);

            using var takeRequest = ConfiguredRequest(
                HttpMethod.Post,
                $"/api/instances/{started.Id}/flows/201",
                new TakeFlowRequest(null));
            using var takeResponse = await client.SendAsync(takeRequest);
            Assert.Equal(HttpStatusCode.OK, takeResponse.StatusCode);

            using var claimRequest = ConfiguredRequest(
                HttpMethod.Post, $"/api/instances/{started.Id}/claim");
            using var claimResponse = await client.SendAsync(claimRequest);
            Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
            var claimed = await claimResponse.Content.ReadFromJsonAsync<InstanceDetailDto>();
            Assert.NotNull(claimed);
            Assert.NotNull(claimed.UserTasks);
            Assert.Equal("canonical-user", claimed.UserTasks.SoleClaimedBy);
            Assert.Contains(claimed.Variables, variable =>
                variable.VariableName == "capturedUser"
                && variable.Value.GetString() == "canonical-user");
            Assert.Contains(claimed.History, item =>
                item.SequenceFlowId == 201 && item.PerformedBy == "canonical-user");

            // A live database edit does not alter the already-started API process.
            setting.Value = "other_identity";
            setting.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            using var latchedRequest = ApiTestAuth.AuthorizeWithClaims(
                new HttpRequestMessage(HttpMethod.Get, "/api/auth/context"),
                "legacy-user",
                [
                    new Claim("preferred_username", "canonical-user"),
                    new Claim("other_identity", "different-user")
                ],
                "Reviewer");
            using var latchedResponse = await client.SendAsync(latchedRequest);
            var latchedContext = await latchedResponse.Content.ReadFromJsonAsync<ActorContextDto>();
            Assert.Equal("canonical-user", latchedContext?.User);
        }
        finally
        {
            db.EngineSettings.Remove(setting);
            await db.SaveChangesAsync();
        }
    }

    private static HttpRequestMessage ConfiguredRequest(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return ApiTestAuth.AuthorizeWithClaims(
            request,
            "legacy-user",
            [new Claim("preferred_username", "canonical-user")],
            "admin",
            "Reviewer");
    }

    private sealed class IdentityApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Flowbit"] = connectionString,
                    ["Jwt:Issuer"] = ApiTestAuth.Issuer,
                    ["Jwt:Audience"] = ApiTestAuth.Audience,
                    ["Jwt:Key"] = ApiTestAuth.Key,
                    ["Serilog:WriteTo:0:Name"] = "Console"
                });
            });
        }
    }
}
