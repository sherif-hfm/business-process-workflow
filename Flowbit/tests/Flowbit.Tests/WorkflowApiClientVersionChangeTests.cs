extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowApiClientVersionChangeTests
{
    [Fact]
    public void SharedContracts_ExposePreviewConcurrencyAndImmutableHistory()
    {
        Assert.Equal(
            new[]
            {
                "InstanceId",
                "SourceWorkflow",
                "TargetWorkflow",
                "Direction",
                "Compatible",
                "Blockers",
                "Warnings",
                "ExpectedSourceWorkflowId",
                "ExpectedUpdatedAt"
            },
            typeof(InstanceVersionChangePreviewDto).GetProperties().Select(property => property.Name));
        Assert.Contains(
            typeof(InstanceDetailDto).GetProperties(),
            property => property.Name == "VersionChanges"
                        && property.PropertyType == typeof(IReadOnlyList<InstanceVersionChangeAuditDto>));
        Assert.Equal("upgrade", InstanceVersionChangeDirections.Upgrade);
        Assert.Equal("downgrade", InstanceVersionChangeDirections.Downgrade);
    }

    [Fact]
    public async Task Preview_PostsTargetAndDeserializesCompatibilityResult()
    {
        var now = DateTimeOffset.Parse("2026-08-02T12:30:00Z");
        var source = Workflow(11, 1, now.AddDays(-1));
        var target = Workflow(13, 3, now);
        var expected = new InstanceVersionChangePreviewDto(
            41,
            source,
            target,
            InstanceVersionChangeDirections.Upgrade,
            false,
            [new("active_node_changed", "The active node type changed.", "token", 91, 7, VariableName: "amount")],
            [new("target_actions_changed", "Available actions will change.", NodeId: 7)],
            source.Id,
            now);
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(expected)
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var actual = await client.PreviewInstanceVersionChangeAsync(
            41,
            new PreviewInstanceVersionChangeRequest(target.Id));

        Assert.NotNull(actual);
        Assert.False(actual.Compatible);
        Assert.Equal("active_node_changed", Assert.Single(actual.Blockers).Code);
        Assert.Equal("amount", Assert.Single(actual.Blockers).VariableName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/instances/41/version-change/preview", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(target.Id, body.RootElement.GetProperty("targetWorkflowId").GetInt64());
    }

    [Fact]
    public async Task Change_PostsOptimisticConcurrencyAndReason()
    {
        var expectedUpdatedAt = DateTimeOffset.Parse("2026-08-02T13:45:12.345Z");
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        await client.ChangeInstanceVersionAsync(
            72,
            new ChangeInstanceVersionRequest(19, 17, expectedUpdatedAt, "Urgent production correction"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/instances/72/version-change", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal(19, root.GetProperty("targetWorkflowId").GetInt64());
        Assert.Equal(17, root.GetProperty("expectedSourceWorkflowId").GetInt64());
        Assert.Equal(expectedUpdatedAt, root.GetProperty("expectedUpdatedAt").GetDateTimeOffset());
        Assert.Equal("Urgent production correction", root.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task WorkflowVersions_ExposesForbiddenStatusForAuthorizationAwareUi()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("Forbidden", Encoding.UTF8, "text/plain")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var error = await Assert.ThrowsAsync<WorkflowApiException>(() =>
            client.GetWorkflowVersionsAsync("purchase request"));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(
            "/api/workflows/purchase%20request/versions",
            Assert.Single(handler.Requests).Path);
    }

    private static WorkflowSummaryDto Workflow(long id, int version, DateTimeOffset createdAt) =>
        new(id, "Purchase request", "purchase-request", version, true, false, createdAt);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                body));
            return responseFactory(request);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);
}
