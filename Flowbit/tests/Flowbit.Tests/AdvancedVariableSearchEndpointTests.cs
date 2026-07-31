using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Flowbit.Api.Auth;
using Flowbit.Api.Endpoints;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class AdvancedVariableSearchEndpointTests
{
    private const long SearchBodyLimit = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AllFiveSearchRoutesExposePostAndThe64KiBRequestLimit()
    {
        using var harness = CreateHarness();
        var endpoints = harness.Factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var authenticatedRoutes = new[]
        {
            "/api/instances/search",
            "/api/instances/inbox/search",
            "/api/user-tasks/manage/search",
            "/api/node-executions/search"
        };

        foreach (var route in authenticatedRoutes)
        {
            var endpoint = Assert.Single(endpoints, item =>
                string.Equals(item.RoutePattern.RawText, route, StringComparison.Ordinal));
            Assert.Contains(
                HttpMethods.Post,
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            Assert.Equal(
                SearchBodyLimit,
                endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize);
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        }

        const string distributionRoute =
            "/api/task-distribution/workflows/{workflowKey}/tasks/search";
        var distribution = Assert.Single(endpoints, item =>
            string.Equals(
                item.RoutePattern.RawText,
                distributionRoute,
                StringComparison.Ordinal));
        Assert.Contains(
            HttpMethods.Post,
            distribution.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        Assert.Equal(
            SearchBodyLimit,
            distribution.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize);
        Assert.NotNull(distribution.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Theory]
    [InlineData("/api/instances/search")]
    [InlineData("/api/instances/inbox/search")]
    [InlineData("/api/user-tasks/manage/search")]
    [InlineData("/api/node-executions/search")]
    public async Task JwtProtectedSearchRoutesRejectUnauthenticatedRequests(string path)
    {
        using var harness = CreateHarness();
        using var response = await harness.Client.PostAsync(path, Json("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EmptyBodiesUseTheExistingPageDefaultsOnJwtSearchRoutes()
    {
        using var harness = CreateHarness();
        var paths = new[]
        {
            "/api/instances/search",
            "/api/instances/inbox/search",
            "/api/user-tasks/manage/search",
            "/api/node-executions/search"
        };

        foreach (var path in paths)
        {
            using var request = AuthorizedPost(path, "{}");
            using var response = await harness.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Equal(1, document.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(50, document.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("items").ValueKind);
            Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("totalCount").ValueKind);
        }

        using var distributionResponse = await harness.Client.PostAsync(
            "/api/task-distribution/workflows/health-certificate/tasks/search",
            Json("{}"));
        Assert.Equal(HttpStatusCode.OK, distributionResponse.StatusCode);
        using var distributionDocument = JsonDocument.Parse(
            await distributionResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, distributionDocument.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, distributionDocument.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task AuthorizedInstanceSearchRejectsBodiesLargerThan64KiB()
    {
        await using var harness = await CreateKestrelHarnessAsync();
        var oversizedJson = JsonSerializer.Serialize(new
        {
            workflowKey = "health-certificate",
            padding = new string('x', (int)SearchBodyLimit)
        });
        Assert.True(Encoding.UTF8.GetByteCount(oversizedJson) > SearchBodyLimit);
        using var request = AuthorizedPost("/api/instances/search", oversizedJson);

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(harness.Workflow.InvocationsFor(
            nameof(IWorkflowEngineService.SearchInstancesAsync)));
    }

    [Theory]
    [InlineData("/api/instances/", "/api/instances/search")]
    [InlineData("/api/instances/inbox", "/api/instances/inbox/search")]
    [InlineData("/api/user-tasks/manage", "/api/user-tasks/manage/search")]
    [InlineData("/api/node-executions/", "/api/node-executions/search")]
    public async Task EmptyPostMatchesTheExistingJwtGetResponse(
        string getPath,
        string postPath)
    {
        using var harness = CreateHarness();
        using var getRequest = AuthorizedRequest(HttpMethod.Get, getPath);
        using var postRequest = AuthorizedPost(postPath, "{}");

        using var getResponse = await harness.Client.SendAsync(getRequest);
        using var postResponse = await harness.Client.SendAsync(postRequest);

        await AssertPagedParityAsync(getResponse, postResponse);
    }

    [Fact]
    public async Task EmptyDistributionPostMatchesGetWithTheSameRouteKeyAndCredentials()
    {
        using var harness = CreateHarness();
        const string getPath =
            "/api/task-distribution/workflows/health-certificate/tasks";
        const string postPath =
            "/api/task-distribution/workflows/health-certificate/tasks/search";
        using var getRequest = DistributionRequest(HttpMethod.Get, getPath);
        using var postRequest = DistributionRequest(HttpMethod.Post, postPath, "{}");

        using var getResponse = await harness.Client.SendAsync(getRequest);
        using var postResponse = await harness.Client.SendAsync(postRequest);

        await AssertPagedParityAsync(getResponse, postResponse);
        var getInvocation = harness.Workflow.SingleInvocation(
            nameof(IWorkflowEngineService.ListDistributableUserTasksAsync));
        var postInvocation = harness.Workflow.SingleInvocation(
            nameof(IWorkflowEngineService.SearchDistributableUserTasksAsync));
        Assert.Equal(getInvocation.Arguments[0], postInvocation.Arguments[0]);
        Assert.Equal(getInvocation.Arguments[1], postInvocation.Arguments[1]);
    }

    [Fact]
    public async Task InstanceSearchForwardsNativeSelectorsAdvancedFilterAndStructuredSort()
    {
        using var harness = CreateHarness();
        using var request = AuthorizedPost(
            "/api/instances/search",
            """
            {
              "status": "running",
              "instanceId": 101,
              "workflowId": 202,
              "workflowKey": "health-certificate",
              "businessKey": "HC-7",
              "nodeId": 303,
              "nodeExternalId": "MEDICAL_REVIEW",
              "variableFilter": {
                "request.medicalCenter.id": { "$eq": "MC-1042" }
              },
              "sort": [
                { "field": "updatedAt", "direction": "desc" },
                { "field": "id", "direction": "asc" }
              ],
              "cursor": "opaque-cursor",
              "includeVariables": true,
              "page": 1,
              "pageSize": 25
            }
            """);

        using var response = await harness.Client.SendAsync(request);

        AssertPagedResponse(response, 1, 25);
        var invocation = harness.Workflow.SingleInvocation(
            nameof(IWorkflowEngineService.SearchInstancesAsync));
        var actor = Assert.IsType<ActorContext>(invocation.Arguments[0]);
        var body = Assert.IsType<InstanceSearchRequest>(invocation.Arguments[1]);
        Assert.Equal("medical-user", actor.User);
        Assert.Equal("running", body.Status);
        Assert.Equal(101, body.InstanceId);
        Assert.Equal(202, body.WorkflowId);
        Assert.Equal("health-certificate", body.WorkflowKey);
        Assert.Equal("HC-7", body.BusinessKey);
        Assert.Equal(303, body.NodeId);
        Assert.Equal("MEDICAL_REVIEW", body.NodeExternalId);
        Assert.Equal("MC-1042", body.VariableFilter!.Value
            .GetProperty("request.medicalCenter.id")
            .GetProperty("$eq")
            .GetString());
        Assert.Collection(
            body.Sort!,
            item =>
            {
                Assert.Equal("updatedAt", item.Field);
                Assert.Equal("desc", item.Direction);
            },
            item =>
            {
                Assert.Equal("id", item.Field);
                Assert.Equal("asc", item.Direction);
            });
        Assert.Equal("opaque-cursor", body.Cursor);
        Assert.True(body.IncludeVariables);
        Assert.Equal(1, body.Page);
        Assert.Equal(25, body.PageSize);
    }

    [Fact]
    public async Task InboxAndManagerSearchesForwardTheirEndpointSpecificSelectors()
    {
        using var harness = CreateHarness();
        using (var inboxRequest = AuthorizedPost(
                   "/api/instances/inbox/search",
                   """
                   {
                     "instanceId": 401,
                     "workflowId": 402,
                     "workflowKey": "health-certificate",
                     "businessKey": "HC-8",
                     "nodeId": 403,
                     "nodeExternalId": "CENTER_REVIEW",
                     "variableFilter": { "center.id": { "$eqIgnoreCase": "mc-7" } },
                     "sort": [{ "field": "taskUpdatedAt", "direction": "desc" }],
                     "includeVariables": true,
                     "page": 2,
                     "pageSize": 30
                   }
                   """))
        using (var inboxResponse = await harness.Client.SendAsync(inboxRequest))
        {
            AssertPagedResponse(inboxResponse, 2, 30);
        }

        using (var managerRequest = AuthorizedPost(
                   "/api/user-tasks/manage/search",
                   """
                   {
                     "taskId": 501,
                     "instanceId": 502,
                     "workflowId": 503,
                     "workflowKey": "health-certificate",
                     "businessKey": "HC-9",
                     "nodeId": 504,
                     "nodeExternalId": "CENTER_ASSIGNMENT",
                     "owner": "doctor-7",
                     "ownership": "assigned",
                     "variableFilter": { "priority": { "$gte": 4 } },
                     "page": 3,
                     "pageSize": 35
                   }
                   """))
        using (var managerResponse = await harness.Client.SendAsync(managerRequest))
        {
            AssertPagedResponse(managerResponse, 3, 35);
        }

        var inboxInvocation = harness.Workflow.SingleInvocation(
            nameof(IWorkflowEngineService.SearchInboxAsync));
        var inbox = Assert.IsType<InboxSearchRequest>(inboxInvocation.Arguments[1]);
        Assert.Equal(401, inbox.InstanceId);
        Assert.Equal(402, inbox.WorkflowId);
        Assert.Equal("health-certificate", inbox.WorkflowKey);
        Assert.Equal("HC-8", inbox.BusinessKey);
        Assert.Equal(403, inbox.NodeId);
        Assert.Equal("CENTER_REVIEW", inbox.NodeExternalId);
        Assert.Equal("mc-7", inbox.VariableFilter!.Value
            .GetProperty("center.id")
            .GetProperty("$eqIgnoreCase")
            .GetString());
        var inboxSort = Assert.Single(inbox.Sort!);
        Assert.Equal("taskUpdatedAt", inboxSort.Field);
        Assert.Equal("desc", inboxSort.Direction);
        Assert.True(inbox.IncludeVariables);
        Assert.Equal(2, inbox.Page);
        Assert.Equal(30, inbox.PageSize);

        var managerInvocation = harness.Workflow.SingleInvocation(
            nameof(IWorkflowEngineService.SearchManageableUserTasksAsync));
        var manager = Assert.IsType<ManageableUserTaskSearchRequest>(
            managerInvocation.Arguments[1]);
        Assert.Equal(501, manager.TaskId);
        Assert.Equal(502, manager.InstanceId);
        Assert.Equal(503, manager.WorkflowId);
        Assert.Equal("health-certificate", manager.WorkflowKey);
        Assert.Equal("HC-9", manager.BusinessKey);
        Assert.Equal(504, manager.NodeId);
        Assert.Equal("CENTER_ASSIGNMENT", manager.NodeExternalId);
        Assert.Equal("doctor-7", manager.Owner);
        Assert.Equal("assigned", manager.Ownership);
        Assert.Equal(4, manager.VariableFilter!.Value
            .GetProperty("priority")
            .GetProperty("$gte")
            .GetInt32());
        Assert.Equal(3, manager.Page);
        Assert.Equal(35, manager.PageSize);
    }

    [Fact]
    public async Task DistributionSearchKeepsWorkflowKeyInRouteAndCredentialsInHeaders()
    {
        using var harness = CreateHarness();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/task-distribution/workflows/health-certificate/tasks/search")
        {
            Content = Json(
                """
                {
                  "taskId": 601,
                  "instanceId": 602,
                  "workflowId": 603,
                  "businessKey": "HC-10",
                  "nodeId": 604,
                  "nodeExternalId": "CENTER_DISTRIBUTION",
                  "owner": "doctor-8",
                  "ownership": "claimed",
                  "variableFilter": { "request.services": { "$contains": "health-certificate" } },
                  "includeVariables": true,
                  "page": 4,
                  "pageSize": 40
                }
                """)
        };
        request.Headers.Add("X-Client-Id", "medical-distributor");
        request.Headers.Add("X-Client-Secret", "distribution-secret");

        using var response = await harness.Client.SendAsync(request);

        AssertPagedResponse(response, 4, 40);
        var invocation = harness.Workflow.SingleInvocation(
            nameof(IWorkflowEngineService.SearchDistributableUserTasksAsync));
        Assert.Equal("health-certificate", invocation.Arguments[0]);
        var credentials = Assert.IsType<TaskDistributionCredentials>(invocation.Arguments[1]);
        Assert.Equal("medical-distributor", credentials.ClientId);
        Assert.Equal("distribution-secret", credentials.ClientSecret);
        var body = Assert.IsType<DistributableUserTaskSearchRequest>(invocation.Arguments[2]);
        Assert.Equal(601, body.TaskId);
        Assert.Equal(602, body.InstanceId);
        Assert.Equal(603, body.WorkflowId);
        Assert.Equal("HC-10", body.BusinessKey);
        Assert.Equal(604, body.NodeId);
        Assert.Equal("CENTER_DISTRIBUTION", body.NodeExternalId);
        Assert.Equal("doctor-8", body.Owner);
        Assert.Equal("claimed", body.Ownership);
        Assert.Equal("health-certificate", body.VariableFilter!.Value
            .GetProperty("request.services")
            .GetProperty("$contains")
            .GetString());
        Assert.True(body.IncludeVariables);
        Assert.Equal(4, body.Page);
        Assert.Equal(40, body.PageSize);
    }

    [Fact]
    public async Task NodeExecutionSearchMapsEveryBodyGroupAndStructuredSortToTheQueryService()
    {
        using var harness = CreateHarness();
        using var request = AuthorizedPost(
            "/api/node-executions/search",
            """
            {
              "executionId": 701,
              "instanceId": 702,
              "workflowId": 703,
              "workflowKey": "health-certificate",
              "workflowVersion": 4,
              "businessKey": "HC-11",
              "tokenId": 704,
              "userTaskId": 705,
              "multiInstanceExecutionId": 706,
              "gatewayBranchId": 707,
              "itemIndex": 2,
              "executionKind": "userTaskItem",
              "nodeId": 708,
              "nodeName": "Medical review",
              "nodeExternalId": "MEDICAL_REVIEW",
              "nodeTypes": ["userTask"],
              "statuses": ["active"],
              "instanceStatuses": ["running"],
              "completionReasons": ["userAction"],
              "isMultiInstance": true,
              "isCutoverSeeded": false,
              "owner": "doctor-9",
              "startedBy": "system",
              "completedBy": "doctor-9",
              "enteredViaFlowId": 801,
              "selectedFlowId": 802,
              "exitedViaFlowId": 803,
              "aggregateFlowId": 804,
              "createdFrom": "2026-07-01T00:00:00Z",
              "createdTo": "2026-08-01T00:00:00Z",
              "minDurationMilliseconds": 10,
              "maxDurationMilliseconds": 5000,
              "variableFilter": { "score": { "$gt": 80 } },
              "sort": [
                { "field": "updatedAt", "direction": "desc" },
                { "field": "id", "direction": "asc" }
              ],
              "page": 5,
              "pageSize": 45
            }
            """);

        using var response = await harness.Client.SendAsync(request);

        AssertPagedResponse(response, 5, 45);
        var invocation = harness.NodeExecutions.SingleInvocation();
        var body = invocation.Request;
        Assert.Equal("medical-user", invocation.Actor.User);
        Assert.Equal(701, body.ExecutionId);
        Assert.Equal(702, body.InstanceId);
        Assert.Equal(703, body.WorkflowId);
        Assert.Equal("health-certificate", body.WorkflowKey);
        Assert.Equal(4, body.WorkflowVersion);
        Assert.Equal("HC-11", body.BusinessKey);
        Assert.Equal(704, body.TokenId);
        Assert.Equal(705, body.UserTaskId);
        Assert.Equal(706, body.MultiInstanceExecutionId);
        Assert.Equal(707, body.GatewayBranchId);
        Assert.Equal(2, body.ItemIndex);
        Assert.Equal("userTaskItem", body.ExecutionKind);
        Assert.Equal(708, body.NodeId);
        Assert.Equal("Medical review", body.NodeName);
        Assert.Equal("MEDICAL_REVIEW", body.NodeExternalId);
        Assert.Equal(["userTask"], body.NodeTypes);
        Assert.Equal(["active"], body.Statuses);
        Assert.Equal(["running"], body.InstanceStatuses);
        Assert.Equal(["userAction"], body.CompletionReasons);
        Assert.True(body.IsMultiInstance);
        Assert.False(body.IsCutoverSeeded);
        Assert.Equal("doctor-9", body.Owner);
        Assert.Equal("system", body.StartedBy);
        Assert.Equal("doctor-9", body.CompletedBy);
        Assert.Equal(801, body.EnteredViaFlowId);
        Assert.Equal(802, body.SelectedFlowId);
        Assert.Equal(803, body.ExitedViaFlowId);
        Assert.Equal(804, body.AggregateFlowId);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            body.CreatedFrom);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            body.CreatedTo);
        Assert.Equal(10, body.MinDurationMilliseconds);
        Assert.Equal(5000, body.MaxDurationMilliseconds);
        Assert.Equal(80, body.VariableFilter!.Value
            .GetProperty("score")
            .GetProperty("$gt")
            .GetInt32());
        Assert.Equal(["updatedAt:desc", "id:asc"], body.Sort);
        Assert.Equal(5, body.Page);
        Assert.Equal(45, body.PageSize);
    }

    private static SearchHarness CreateHarness()
    {
        var factory = new ContractApiFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        return new SearchHarness(
            factory,
            client,
            factory.Workflow,
            factory.NodeExecutions);
    }

    private static async Task<KestrelSearchHarness> CreateKestrelHarnessAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = ContractAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = ContractAuthenticationHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, ContractAuthenticationHandler>(
                ContractAuthenticationHandler.SchemeName,
                _ => { });
        var identityConfiguration = new ActorIdentityConfiguration();
        identityConfiguration.Initialize(null);
        builder.Services.AddSingleton<IActorContextResolver>(
            new ActorContextResolver(identityConfiguration));
        var workflowService = DispatchProxy.Create<
            IWorkflowEngineService,
            RecordingWorkflowServiceProxy>();
        var workflowRecorder = (RecordingWorkflowServiceProxy)(object)workflowService;
        builder.Services.AddSingleton(workflowService);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWorkflowInstanceEndpoints();
        await app.StartAsync();
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        var address = Assert.Single(addresses!);
        return new KestrelSearchHarness(
            app,
            new HttpClient { BaseAddress = new Uri(address) },
            workflowRecorder);
    }

    private static HttpRequestMessage AuthorizedPost(string path, string json)
    {
        var request = AuthorizedRequest(HttpMethod.Post, path);
        request.Content = Json(json);
        return request;
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        return ApiTestAuth.Authorize(request, "medical-user", "admin");
    }

    private static HttpRequestMessage DistributionRequest(
        HttpMethod method,
        string path,
        string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = Json(json);
        }
        request.Headers.Add("X-Client-Id", "medical-distributor");
        request.Headers.Add("X-Client-Secret", "distribution-secret");
        return request;
    }

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static void AssertPagedResponse(
        HttpResponseMessage response,
        int expectedPage,
        int expectedPageSize)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = response.Content
            .ReadFromJsonAsync<JsonElement>(JsonOptions)
            .GetAwaiter()
            .GetResult();
        Assert.Equal(JsonValueKind.Array, result.GetProperty("items").ValueKind);
        Assert.Equal(expectedPage, result.GetProperty("page").GetInt32());
        Assert.Equal(expectedPageSize, result.GetProperty("pageSize").GetInt32());
        Assert.Equal(41, result.GetProperty("totalCount").GetInt64());
    }

    private static async Task AssertPagedParityAsync(
        HttpResponseMessage getResponse,
        HttpResponseMessage postResponse)
    {
        var getBody = await getResponse.Content.ReadAsStringAsync();
        var postBody = await postResponse.Content.ReadAsStringAsync();
        Assert.True(
            getResponse.StatusCode == HttpStatusCode.OK,
            $"GET returned {(int)getResponse.StatusCode}: {getBody}");
        Assert.True(
            getResponse.StatusCode == postResponse.StatusCode,
            $"POST returned {(int)postResponse.StatusCode}: {postBody}");
        using var getDocument = JsonDocument.Parse(getBody);
        using var postDocument = JsonDocument.Parse(postBody);
        var getRoot = getDocument.RootElement;
        var postRoot = postDocument.RootElement;
        Assert.Equal(
            getRoot.GetProperty("page").GetInt32(),
            postRoot.GetProperty("page").GetInt32());
        Assert.Equal(
            getRoot.GetProperty("pageSize").GetInt32(),
            postRoot.GetProperty("pageSize").GetInt32());
        Assert.Equal(
            getRoot.GetProperty("totalCount").GetInt64(),
            postRoot.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            getRoot.GetProperty("items").GetRawText(),
            postRoot.GetProperty("items").GetRawText());
    }

    public sealed record RecordedInvocation(
        string Method,
        IReadOnlyList<object?> Arguments);

    public class RecordingWorkflowServiceProxy : DispatchProxy
    {
        private readonly ConcurrentQueue<RecordedInvocation> invocations = new();

        public RecordedInvocation SingleInvocation(string method) =>
            Assert.Single(invocations, invocation => invocation.Method == method);

        public IReadOnlyList<RecordedInvocation> InvocationsFor(string method) =>
            invocations.Where(invocation => invocation.Method == method).ToArray();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var copiedArguments = args?.ToArray() ?? [];
            invocations.Enqueue(new RecordedInvocation(targetMethod.Name, copiedArguments));

            return targetMethod.Name switch
            {
                nameof(IWorkflowEngineService.ListInstancesAsync) =>
                    Task.FromResult(InstancePage(copiedArguments)),
                nameof(IWorkflowEngineService.SearchInstancesAsync) =>
                    Task.FromResult(InstancePage(
                        Assert.IsType<InstanceSearchRequest>(copiedArguments[1]))),
                nameof(IWorkflowEngineService.GetInboxAsync) =>
                    Task.FromResult(InboxPage(copiedArguments)),
                nameof(IWorkflowEngineService.SearchInboxAsync) =>
                    Task.FromResult(InboxPage(
                        Assert.IsType<InboxSearchRequest>(copiedArguments[1]))),
                nameof(IWorkflowEngineService.ListManageableUserTasksAsync) =>
                    Task.FromResult(ManagedTaskPage(copiedArguments)),
                nameof(IWorkflowEngineService.SearchManageableUserTasksAsync) =>
                    Task.FromResult(ManagedTaskPage(
                        Assert.IsType<ManageableUserTaskSearchRequest>(copiedArguments[1]))),
                nameof(IWorkflowEngineService.ListDistributableUserTasksAsync) =>
                    Task.FromResult<PagedResult<ManagedUserTaskDto>?>(
                        ManagedTaskPage(copiedArguments)),
                nameof(IWorkflowEngineService.SearchDistributableUserTasksAsync) =>
                    Task.FromResult<PagedResult<ManagedUserTaskDto>?>(ManagedTaskPage(
                        Assert.IsType<DistributableUserTaskSearchRequest>(copiedArguments[2]))),
                _ => throw new NotSupportedException(
                    $"The endpoint contract proxy did not expect {targetMethod.Name}.")
            };
        }
    }

    private sealed class RecordingNodeExecutionQueryService : INodeExecutionQueryService
    {
        private readonly ConcurrentQueue<NodeExecutionInvocation> invocations = new();

        public NodeExecutionInvocation SingleInvocation() => Assert.Single(invocations);

        public Task<PagedResult<NodeExecutionSummaryDto>> SearchAsync(
            NodeExecutionSearchRequest request,
            ActorContext actor,
            CancellationToken cancellationToken)
        {
            invocations.Enqueue(new NodeExecutionInvocation(request, actor));
            return Task.FromResult(NodeExecutionPage(request));
        }

        public Task<NodeExecutionDetailDto?> GetAsync(
            long id,
            ActorContext actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record NodeExecutionInvocation(
        NodeExecutionSearchRequest Request,
        ActorContext Actor);

    private sealed class ContractApiFactory : WebApplicationFactory<Program>
    {
        private readonly IWorkflowEngineService workflowService;

        public ContractApiFactory()
        {
            workflowService = DispatchProxy.Create<
                IWorkflowEngineService,
                RecordingWorkflowServiceProxy>();
            Workflow = (RecordingWorkflowServiceProxy)(object)workflowService;
        }

        public RecordingWorkflowServiceProxy Workflow { get; }
        public RecordingNodeExecutionQueryService NodeExecutions { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Flowbit"] =
                        "Host=127.0.0.1;Port=1;Database=flowbit_contract;Username=test;Password=test;Timeout=1;Command Timeout=1",
                    ["Jwt:Issuer"] = ApiTestAuth.Issuer,
                    ["Jwt:Audience"] = ApiTestAuth.Audience,
                    ["Jwt:Key"] = ApiTestAuth.Key,
                    ["Serilog:WriteTo:0:Name"] = "Console"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWorkflowEngineService>();
                services.AddSingleton(workflowService);
                services.RemoveAll<INodeExecutionQueryService>();
                services.AddSingleton<INodeExecutionQueryService>(NodeExecutions);
                services.RemoveAll<IEngineSettingsService>();
                services.AddSingleton<IEngineSettingsService, EmptyEngineSettingsService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = ContractAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = ContractAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, ContractAuthenticationHandler>(
                        ContractAuthenticationHandler.SchemeName,
                        _ => { });
            });
        }
    }

    private sealed class EmptyEngineSettingsService : IEngineSettingsService
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken) =>
            Task.FromResult<EngineSettingRecord?>(null);

        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(
            string pattern,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EngineSettingRecord>>([]);

        public Task<EngineSettingRecord> SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            string key,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ContractAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "FlowbitAdvancedSearchContractTests";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user),
                new(ClaimTypes.NameIdentifier, user)
            };
            var roles = Request.Headers["X-Test-Roles"].FirstOrDefault()
                ?.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? [];
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim("role", role));
            }

            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class SearchHarness(
        ContractApiFactory factory,
        HttpClient client,
        RecordingWorkflowServiceProxy workflow,
        RecordingNodeExecutionQueryService nodeExecutions) : IDisposable
    {
        public ContractApiFactory Factory { get; } = factory;
        public HttpClient Client { get; } = client;
        public RecordingWorkflowServiceProxy Workflow { get; } = workflow;
        public RecordingNodeExecutionQueryService NodeExecutions { get; } = nodeExecutions;

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private sealed class KestrelSearchHarness(
        WebApplication app,
        HttpClient client,
        RecordingWorkflowServiceProxy workflow) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public RecordingWorkflowServiceProxy Workflow { get; } = workflow;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static PagedResult<InstanceSummaryDto> InstancePage(
        InstanceSearchRequest request) =>
        new([], Page(request.Page), PageSize(request.PageSize), 41);

    private static PagedResult<InstanceSummaryDto> InstancePage(
        IReadOnlyList<object?> arguments)
    {
        var (page, pageSize) = TrailingPaging(arguments);
        return new([], page, pageSize, 41);
    }

    private static PagedResult<InboxItemDto> InboxPage(InboxSearchRequest request) =>
        new([], Page(request.Page), PageSize(request.PageSize), 41);

    private static PagedResult<InboxItemDto> InboxPage(
        IReadOnlyList<object?> arguments)
    {
        var (page, pageSize) = TrailingPaging(arguments);
        return new([], page, pageSize, 41);
    }

    private static PagedResult<ManagedUserTaskDto> ManagedTaskPage(
        ManageableUserTaskSearchRequest request) =>
        new([], Page(request.Page), PageSize(request.PageSize), 41);

    private static PagedResult<ManagedUserTaskDto> ManagedTaskPage(
        DistributableUserTaskSearchRequest request) =>
        new([], Page(request.Page), PageSize(request.PageSize), 41);

    private static PagedResult<ManagedUserTaskDto> ManagedTaskPage(
        IReadOnlyList<object?> arguments)
    {
        var (page, pageSize) = TrailingPaging(arguments);
        return new([], page, pageSize, 41);
    }

    private static PagedResult<NodeExecutionSummaryDto> NodeExecutionPage(
        NodeExecutionSearchRequest request) =>
        new([], Page(request.Page), PageSize(request.PageSize), 41);

    private static int Page(int? value) => Math.Max(1, value ?? 1);

    private static int PageSize(int? value) => Math.Clamp(value ?? 50, 1, 200);

    private static (int Page, int PageSize) TrailingPaging(
        IReadOnlyList<object?> arguments)
    {
        var values = arguments
            .Reverse()
            .OfType<int>()
            .Take(2)
            .ToArray();
        Assert.Equal(2, values.Length);
        return (Page(values[1]), PageSize(values[0]));
    }
}

[Collection(PostgresApiCollection.Name)]
public sealed class AdvancedVariableSearchGetPostParityApiTests(
    PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("/api/instances/", "/api/instances/search")]
    [InlineData("/api/instances/inbox", "/api/instances/inbox/search")]
    [InlineData("/api/user-tasks/manage", "/api/user-tasks/manage/search")]
    [InlineData("/api/node-executions/", "/api/node-executions/search")]
    public async Task EmptyPostMatchesGetAgainstPostgreSql(
        string getPath,
        string postPath)
    {
        using var getRequest = AuthorizedRequest(HttpMethod.Get, getPath);
        using var postRequest = AuthorizedRequest(HttpMethod.Post, postPath, "{}");

        using var getResponse = await fixture.Client.SendAsync(getRequest);
        using var postResponse = await fixture.Client.SendAsync(postRequest);

        await AssertPagedParityAsync(getResponse, postResponse);
    }

    [Fact]
    public async Task DistributionEmptyPostMatchesGetForAnAuthenticatedWorkflowFamily()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var clientId = $"distribution-parity-{suffix}";
        var clientSecret = $"distribution-secret-{suffix}";
        var workflow = await CreateDistributionWorkflowAsync(
            suffix,
            clientId,
            clientSecret);
        var basePath =
            $"/api/task-distribution/workflows/{Uri.EscapeDataString(workflow.WorkflowKey)}/tasks";
        using var getRequest = DistributionRequest(
            HttpMethod.Get,
            basePath,
            clientId,
            clientSecret);
        using var postRequest = DistributionRequest(
            HttpMethod.Post,
            $"{basePath}/search",
            clientId,
            clientSecret,
            "{}");

        using var getResponse = await fixture.Client.SendAsync(getRequest);
        using var postResponse = await fixture.Client.SendAsync(postRequest);

        await AssertPagedParityAsync(getResponse, postResponse);
    }

    [Theory]
    [InlineData("""
        { "variableFilter": { "center.id": { "$regex": "MC-.*" } } }
        """)]
    [InlineData("""
        {
          "variableFilter": {
            "$or": [{ "center.id": { "$eq": "MC-1" } }],
            "status": { "$eq": "open" }
          }
        }
        """)]
    public async Task AuthenticatedPostMapsInvalidAdvancedFilterToBadRequest(
        string json)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            "/api/instances/search",
            json);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(
            document.RootElement.GetProperty("error").GetString()));
    }

    private async Task<WorkflowDetailDto> CreateDistributionWorkflowAsync(
        string suffix,
        string clientId,
        string clientSecret)
    {
        var definition = new WorkflowModel
        {
            Id = $"advanced-search-parity-{suffix}",
            Name = $"Advanced search parity {suffix}",
            InitialEventId = 1,
            TaskDistribution = new TaskDistributionModel
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            },
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
                    Name = "Medical center review",
                    ExternalId = "MEDICAL_CENTER_REVIEW",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["MedicalCenter"]
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
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            "/api/workflows",
            JsonSerializer.Serialize(
                new CreateWorkflowRequest(definition, true),
                JsonOptions));
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions)
            ?? throw new InvalidOperationException("Workflow response was empty.");
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return ApiTestAuth.Authorize(request, "advanced-search-parity", "admin");
    }

    private static HttpRequestMessage DistributionRequest(
        HttpMethod method,
        string path,
        string clientId,
        string clientSecret,
        string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        request.Headers.Add("X-Client-Id", clientId);
        request.Headers.Add("X-Client-Secret", clientSecret);
        return request;
    }

    private static async Task AssertPagedParityAsync(
        HttpResponseMessage getResponse,
        HttpResponseMessage postResponse)
    {
        var getBody = await getResponse.Content.ReadAsStringAsync();
        var postBody = await postResponse.Content.ReadAsStringAsync();
        Assert.True(
            getResponse.StatusCode == HttpStatusCode.OK,
            $"GET returned {(int)getResponse.StatusCode}: {getBody}");
        Assert.True(
            getResponse.StatusCode == postResponse.StatusCode,
            $"POST returned {(int)postResponse.StatusCode}: {postBody}");
        using var getDocument = JsonDocument.Parse(getBody);
        using var postDocument = JsonDocument.Parse(postBody);
        var getRoot = getDocument.RootElement;
        var postRoot = postDocument.RootElement;
        Assert.Equal(1, getRoot.GetProperty("page").GetInt32());
        Assert.Equal(50, getRoot.GetProperty("pageSize").GetInt32());
        Assert.Equal(
            getRoot.GetProperty("page").GetInt32(),
            postRoot.GetProperty("page").GetInt32());
        Assert.Equal(
            getRoot.GetProperty("pageSize").GetInt32(),
            postRoot.GetProperty("pageSize").GetInt32());
        Assert.Equal(
            getRoot.GetProperty("totalCount").GetInt64(),
            postRoot.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            getRoot.GetProperty("items").GetRawText(),
            postRoot.GetProperty("items").GetRawText());
    }
}
