extern alias FlowbitUi;

using System.Net;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowApiClientAdvancedSearchTests
{
    [Fact]
    public async Task InstanceSearch_PostsEverySelectorStructuredSortCursorPagingAndVariables()
    {
        using var harness = CreateHarness();

        await harness.Client.SearchInstancesAsync(new InstanceSearchRequest
        {
            Status = "running",
            InstanceId = 42,
            WorkflowId = 17,
            WorkflowKey = "health-certificate",
            BusinessKey = "HC-42",
            NodeId = 3,
            NodeExternalId = "MEDICAL_REVIEW",
            VariableFilter = Filter("""{"request.medicalCenter.id":{"$eq":"MC-1042"}}"""),
            Sort =
            [
                new SearchSortDto("updatedAt", "desc"),
                new SearchSortDto("id", "asc")
            ],
            Cursor = "cursor-token",
            IncludeVariables = true,
            Page = 2,
            PageSize = 25
        });

        var request = Assert.Single(harness.Handler.Requests);
        AssertRequest(request, "/api/instances/search");
        using var body = ParseBody(request);
        var root = body.RootElement;
        Assert.Equal("running", root.GetProperty("status").GetString());
        Assert.Equal(42, root.GetProperty("instanceId").GetInt64());
        Assert.Equal(17, root.GetProperty("workflowId").GetInt64());
        Assert.Equal("health-certificate", root.GetProperty("workflowKey").GetString());
        Assert.Equal("HC-42", root.GetProperty("businessKey").GetString());
        Assert.Equal(3, root.GetProperty("nodeId").GetInt32());
        Assert.Equal("MEDICAL_REVIEW", root.GetProperty("nodeExternalId").GetString());
        Assert.Equal("MC-1042", root.GetProperty("variableFilter")
            .GetProperty("request.medicalCenter.id").GetProperty("$eq").GetString());
        AssertSorts(root, ("updatedAt", "desc"), ("id", "asc"));
        Assert.Equal("cursor-token", root.GetProperty("cursor").GetString());
        Assert.True(root.GetProperty("includeVariables").GetBoolean());
        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(25, root.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task InboxSearch_PostsEverySelectorStructuredSortPagingAndVariables()
    {
        using var harness = CreateHarness();

        await harness.Client.SearchInboxAsync(new InboxSearchRequest
        {
            InstanceId = 51,
            WorkflowId = 18,
            WorkflowKey = "health-renewal",
            BusinessKey = "HR-51",
            NodeId = 4,
            NodeExternalId = "CENTER_INBOX",
            VariableFilter = Filter("""{"request.services":{"$contains":"renewal"}}"""),
            Sort =
            [
                new SearchSortDto("taskCreatedAt", "asc"),
                new SearchSortDto("userTaskId", "desc")
            ],
            IncludeVariables = false,
            Page = 3,
            PageSize = 40
        });

        var request = Assert.Single(harness.Handler.Requests);
        AssertRequest(request, "/api/instances/inbox/search");
        using var body = ParseBody(request);
        var root = body.RootElement;
        Assert.Equal(51, root.GetProperty("instanceId").GetInt64());
        Assert.Equal(18, root.GetProperty("workflowId").GetInt64());
        Assert.Equal("health-renewal", root.GetProperty("workflowKey").GetString());
        Assert.Equal("HR-51", root.GetProperty("businessKey").GetString());
        Assert.Equal(4, root.GetProperty("nodeId").GetInt32());
        Assert.Equal("CENTER_INBOX", root.GetProperty("nodeExternalId").GetString());
        Assert.Equal("renewal", root.GetProperty("variableFilter")
            .GetProperty("request.services").GetProperty("$contains").GetString());
        AssertSorts(root, ("taskCreatedAt", "asc"), ("userTaskId", "desc"));
        Assert.False(root.GetProperty("includeVariables").GetBoolean());
        Assert.Equal(3, root.GetProperty("page").GetInt32());
        Assert.Equal(40, root.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task ManageableTaskSearch_PostsEverySelectorOwnershipPagingAndVariables()
    {
        using var harness = CreateHarness();

        await harness.Client.SearchManagedUserTasksAsync(new ManageableUserTaskSearchRequest
        {
            TaskId = 81,
            InstanceId = 42,
            WorkflowId = 17,
            WorkflowKey = "health-certificate",
            BusinessKey = "HC-42",
            NodeId = 5,
            NodeExternalId = "ASSIGN_CERTIFICATE",
            Owner = "center-user",
            Ownership = "assigned",
            VariableFilter = Filter("""{"priority":{"$in":[1,2,3]}}"""),
            Page = 3,
            PageSize = 20
        });

        var request = Assert.Single(harness.Handler.Requests);
        AssertRequest(request, "/api/user-tasks/manage/search");
        using var body = ParseBody(request);
        var root = body.RootElement;
        Assert.Equal(81, root.GetProperty("taskId").GetInt64());
        Assert.Equal(42, root.GetProperty("instanceId").GetInt64());
        Assert.Equal(17, root.GetProperty("workflowId").GetInt64());
        Assert.Equal("health-certificate", root.GetProperty("workflowKey").GetString());
        Assert.Equal("HC-42", root.GetProperty("businessKey").GetString());
        Assert.Equal(5, root.GetProperty("nodeId").GetInt32());
        Assert.Equal("ASSIGN_CERTIFICATE", root.GetProperty("nodeExternalId").GetString());
        Assert.Equal("center-user", root.GetProperty("owner").GetString());
        Assert.Equal("assigned", root.GetProperty("ownership").GetString());
        Assert.Equal(3, root.GetProperty("variableFilter")
            .GetProperty("priority").GetProperty("$in").GetArrayLength());
        Assert.Equal(3, root.GetProperty("page").GetInt32());
        Assert.Equal(20, root.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task DistributionSearch_PostsEverySelectorAndKeepsCredentialsOnlyInHeaders()
    {
        using var harness = CreateHarness();
        const string workflowKey = " health/certificate + v1 ";
        const string clientId = "medical-center-client-47";
        const string secret = "distribution-secret-92";

        await harness.Client.SearchDistributableUserTasksAsync(
            workflowKey,
            clientId,
            secret,
            new DistributableUserTaskSearchRequest
            {
                TaskId = 91,
                InstanceId = 52,
                WorkflowId = 19,
                BusinessKey = "HC-52",
                NodeId = 6,
                NodeExternalId = "DISTRIBUTE_CERTIFICATE",
                Owner = "distribution-user",
                Ownership = "unassigned",
                VariableFilter = Filter("""{"center":{"$eqIgnoreCase":"MC-1042"}}"""),
                IncludeVariables = true,
                Page = 4,
                PageSize = 30
            });

        var request = Assert.Single(harness.Handler.Requests);
        AssertRequest(
            request,
            "/api/task-distribution/workflows/health%2Fcertificate%20%2B%20v1/tasks/search");
        Assert.Equal(clientId, request.Headers["X-Client-Id"]);
        Assert.Equal(secret, request.Headers["X-Client-Secret"]);
        AssertCredentialIsNotInUrlOrBody(request, clientId);
        AssertCredentialIsNotInUrlOrBody(request, secret);

        using var body = ParseBody(request);
        var root = body.RootElement;
        Assert.Equal(91, root.GetProperty("taskId").GetInt64());
        Assert.Equal(52, root.GetProperty("instanceId").GetInt64());
        Assert.Equal(19, root.GetProperty("workflowId").GetInt64());
        Assert.Equal("HC-52", root.GetProperty("businessKey").GetString());
        Assert.Equal(6, root.GetProperty("nodeId").GetInt32());
        Assert.Equal("DISTRIBUTE_CERTIFICATE", root.GetProperty("nodeExternalId").GetString());
        Assert.Equal("distribution-user", root.GetProperty("owner").GetString());
        Assert.Equal("unassigned", root.GetProperty("ownership").GetString());
        Assert.Equal("MC-1042", root.GetProperty("variableFilter")
            .GetProperty("center").GetProperty("$eqIgnoreCase").GetString());
        Assert.True(root.GetProperty("includeVariables").GetBoolean());
        Assert.Equal(4, root.GetProperty("page").GetInt32());
        Assert.Equal(30, root.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task NodeExecutionSearch_PostsEverySelectorRangeStructuredSortPagingAndVariables()
    {
        using var harness = CreateHarness();
        var createdFrom = DateTimeOffset.Parse("2026-08-01T00:00:00+03:00");
        var createdTo = DateTimeOffset.Parse("2026-08-02T00:00:00+03:00");
        var startedFrom = DateTimeOffset.Parse("2026-08-03T00:00:00+03:00");
        var startedTo = DateTimeOffset.Parse("2026-08-04T00:00:00+03:00");
        var updatedFrom = DateTimeOffset.Parse("2026-08-05T00:00:00+03:00");
        var updatedTo = DateTimeOffset.Parse("2026-08-06T00:00:00+03:00");
        var completedFrom = DateTimeOffset.Parse("2026-08-07T00:00:00+03:00");
        var completedTo = DateTimeOffset.Parse("2026-08-08T00:00:00+03:00");

        await harness.Client.SearchNodeExecutionsAsync(new NodeExecutionSearchBodyRequest
        {
            ExecutionId = 9001,
            InstanceId = 101,
            WorkflowId = 22,
            WorkflowKey = "health-certificate",
            WorkflowVersion = 3,
            BusinessKey = "HC-101",
            TokenId = 201,
            UserTaskId = 301,
            MultiInstanceExecutionId = 401,
            GatewayBranchId = 501,
            ItemIndex = 7,
            ExecutionKind = "userTaskItem",
            NodeId = 8,
            NodeName = "Medical center review",
            NodeExternalId = "MEDICAL_CENTER_REVIEW",
            NodeTypes = ["userTask", "serviceTask"],
            Statuses = ["active", "completed"],
            InstanceStatuses = ["Running", "Completed"],
            CompletionReasons = ["userAction", "normal"],
            IsMultiInstance = true,
            IsCutoverSeeded = false,
            Owner = "center-user",
            StartedBy = "starter-user",
            CompletedBy = "completer-user",
            EnteredViaFlowId = 601,
            SelectedFlowId = 602,
            ExitedViaFlowId = 603,
            AggregateFlowId = 604,
            CreatedFrom = createdFrom,
            CreatedTo = createdTo,
            StartedFrom = startedFrom,
            StartedTo = startedTo,
            UpdatedFrom = updatedFrom,
            UpdatedTo = updatedTo,
            CompletedFrom = completedFrom,
            CompletedTo = completedTo,
            MinDurationMilliseconds = 1000,
            MaxDurationMilliseconds = 9000,
            VariableFilter = Filter("""{"score":{"$gte":75}}"""),
            Sort =
            [
                new SearchSortDto("duration", "asc"),
                new SearchSortDto("id", "desc")
            ],
            Page = 4,
            PageSize = 10
        });

        var request = Assert.Single(harness.Handler.Requests);
        AssertRequest(request, "/api/node-executions/search");
        using var body = ParseBody(request);
        var root = body.RootElement;
        Assert.Equal(9001, root.GetProperty("executionId").GetInt64());
        Assert.Equal(101, root.GetProperty("instanceId").GetInt64());
        Assert.Equal(22, root.GetProperty("workflowId").GetInt64());
        Assert.Equal("health-certificate", root.GetProperty("workflowKey").GetString());
        Assert.Equal(3, root.GetProperty("workflowVersion").GetInt32());
        Assert.Equal("HC-101", root.GetProperty("businessKey").GetString());
        Assert.Equal(201, root.GetProperty("tokenId").GetInt64());
        Assert.Equal(301, root.GetProperty("userTaskId").GetInt64());
        Assert.Equal(401, root.GetProperty("multiInstanceExecutionId").GetInt64());
        Assert.Equal(501, root.GetProperty("gatewayBranchId").GetInt64());
        Assert.Equal(7, root.GetProperty("itemIndex").GetInt32());
        Assert.Equal("userTaskItem", root.GetProperty("executionKind").GetString());
        Assert.Equal(8, root.GetProperty("nodeId").GetInt32());
        Assert.Equal("Medical center review", root.GetProperty("nodeName").GetString());
        Assert.Equal("MEDICAL_CENTER_REVIEW", root.GetProperty("nodeExternalId").GetString());
        AssertStringArray(root, "nodeTypes", "userTask", "serviceTask");
        AssertStringArray(root, "statuses", "active", "completed");
        AssertStringArray(root, "instanceStatuses", "Running", "Completed");
        AssertStringArray(root, "completionReasons", "userAction", "normal");
        Assert.True(root.GetProperty("isMultiInstance").GetBoolean());
        Assert.False(root.GetProperty("isCutoverSeeded").GetBoolean());
        Assert.Equal("center-user", root.GetProperty("owner").GetString());
        Assert.Equal("starter-user", root.GetProperty("startedBy").GetString());
        Assert.Equal("completer-user", root.GetProperty("completedBy").GetString());
        Assert.Equal(601, root.GetProperty("enteredViaFlowId").GetInt32());
        Assert.Equal(602, root.GetProperty("selectedFlowId").GetInt32());
        Assert.Equal(603, root.GetProperty("exitedViaFlowId").GetInt32());
        Assert.Equal(604, root.GetProperty("aggregateFlowId").GetInt32());
        Assert.Equal(createdFrom, root.GetProperty("createdFrom").GetDateTimeOffset());
        Assert.Equal(createdTo, root.GetProperty("createdTo").GetDateTimeOffset());
        Assert.Equal(startedFrom, root.GetProperty("startedFrom").GetDateTimeOffset());
        Assert.Equal(startedTo, root.GetProperty("startedTo").GetDateTimeOffset());
        Assert.Equal(updatedFrom, root.GetProperty("updatedFrom").GetDateTimeOffset());
        Assert.Equal(updatedTo, root.GetProperty("updatedTo").GetDateTimeOffset());
        Assert.Equal(completedFrom, root.GetProperty("completedFrom").GetDateTimeOffset());
        Assert.Equal(completedTo, root.GetProperty("completedTo").GetDateTimeOffset());
        Assert.Equal(1000, root.GetProperty("minDurationMilliseconds").GetInt64());
        Assert.Equal(9000, root.GetProperty("maxDurationMilliseconds").GetInt64());
        Assert.Equal(75, root.GetProperty("variableFilter")
            .GetProperty("score").GetProperty("$gte").GetInt32());
        AssertSorts(root, ("duration", "asc"), ("id", "desc"));
        Assert.Equal(4, root.GetProperty("page").GetInt32());
        Assert.Equal(10, root.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task EmptySearchRequests_StillPostAllFiveRoutesWithEndpointDefaults()
    {
        using var harness = CreateHarness();

        var instances = await harness.Client.SearchInstancesAsync(new InstanceSearchRequest());
        var inbox = await harness.Client.SearchInboxAsync(new InboxSearchRequest());
        var managed = await harness.Client.SearchManagedUserTasksAsync(new ManageableUserTaskSearchRequest());
        var distributed = await harness.Client.SearchDistributableUserTasksAsync(
            "empty workflow",
            null,
            null,
            new DistributableUserTaskSearchRequest());
        var executions = await harness.Client.SearchNodeExecutionsAsync(new NodeExecutionSearchBodyRequest());

        Assert.Equal(1, instances.Page);
        Assert.Equal(50, instances.PageSize);
        Assert.Equal(1, inbox.Page);
        Assert.Equal(50, inbox.PageSize);
        Assert.Equal(1, managed.Page);
        Assert.Equal(50, managed.PageSize);
        Assert.Equal(1, distributed.Page);
        Assert.Equal(50, distributed.PageSize);
        Assert.Equal(1, executions.Page);
        Assert.Equal(50, executions.PageSize);

        Assert.Collection(
            harness.Handler.Requests,
            request => AssertEmptySearchRequest(request, "/api/instances/search"),
            request => AssertEmptySearchRequest(request, "/api/instances/inbox/search"),
            request => AssertEmptySearchRequest(request, "/api/user-tasks/manage/search"),
            request =>
            {
                AssertEmptySearchRequest(
                    request,
                    "/api/task-distribution/workflows/empty%20workflow/tasks/search");
                Assert.DoesNotContain("X-Client-Id", request.Headers.Keys);
                Assert.DoesNotContain("X-Client-Secret", request.Headers.Keys);
            },
            request => AssertEmptySearchRequest(request, "/api/node-executions/search"));
    }

    [Fact]
    public async Task ValidButUnsupportedOperator_ReachesApiAndSurfacesCleanBadRequestMessage()
    {
        using var harness = CreateHarness(
            HttpStatusCode.BadRequest,
            """{"error":"Unknown variable-filter operator '$regex'."}""");

        var exception = await Assert.ThrowsAsync<WorkflowApiException>(() =>
            harness.Client.SearchInboxAsync(new InboxSearchRequest
            {
                VariableFilter = Filter("""{"center":{"$regex":"MC-.*"}}""")
            }));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("Unknown variable-filter operator '$regex'.", exception.Message);
        AssertRequest(Assert.Single(harness.Handler.Requests), "/api/instances/inbox/search");
    }

    private static void AssertRequest(CapturedRequest request, string route)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(route, request.PathAndQuery);
        Assert.NotNull(request.Body);
    }

    private static JsonDocument ParseBody(CapturedRequest request) =>
        JsonDocument.Parse(Assert.IsType<string>(request.Body));

    private static void AssertEmptySearchRequest(CapturedRequest request, string route)
    {
        AssertRequest(request, route);
        using var body = ParseBody(request);
        Assert.Equal(JsonValueKind.Object, body.RootElement.ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("variableFilter").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("page").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("pageSize").ValueKind);
    }

    private static void AssertCredentialIsNotInUrlOrBody(CapturedRequest request, string credential)
    {
        Assert.DoesNotContain(credential, request.PathAndQuery, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, request.Body!, StringComparison.Ordinal);
    }

    private static void AssertSorts(JsonElement root, params (string Field, string Direction)[] expected)
    {
        var actual = root.GetProperty("sort").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Field, actual[index].GetProperty("field").GetString());
            Assert.Equal(expected[index].Direction, actual[index].GetProperty("direction").GetString());
        }
    }

    private static void AssertStringArray(JsonElement root, string property, params string[] expected)
    {
        var actual = root.GetProperty(property)
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static JsonElement Filter(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ClientHarness CreateHarness(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseBody = null)
    {
        var handler = new RecordingHandler(statusCode, responseBody);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://flowbit.test")
        };
        return new ClientHarness(httpClient, handler, new WorkflowApiClient(httpClient));
    }

    private sealed class ClientHarness(
        HttpClient httpClient,
        RecordingHandler handler,
        WorkflowApiClient client) : IDisposable
    {
        public RecordingHandler Handler { get; } = handler;
        public WorkflowApiClient Client { get; } = client;
        public void Dispose() => httpClient.Dispose();
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        string? responseBody) : HttpMessageHandler
    {
        private const string EmptyPage =
            """{"items":[],"page":1,"pageSize":50,"totalCount":0,"nextCursor":null}""";

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers
                .ToDictionary(
                    pair => pair.Key,
                    pair => string.Join(",", pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                body,
                headers));

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody ?? EmptyPage,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string PathAndQuery,
        string? Body,
        IReadOnlyDictionary<string, string> Headers);
}
