extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowApiClientAdministrativeActionTests
{
    [Fact]
    public async Task OrdinaryTaskAuthorizationFailureUsesWorkflowApiExceptionForPrivilegedFallback()
    {
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.BadRequest, new { error = "Task is not assigned to this actor." }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var exception = await Assert.ThrowsAsync<WorkflowApiException>(
            () => client.GetUserTaskAsync(91));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("not assigned", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/api/user-tasks/91", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task AdministrativeWorkflowCatalog_UsesPrivilegedCatalogRoute()
    {
        var version = new WorkflowSummaryDto(
            8,
            "Purchase request",
            "purchase-request",
            3,
            true,
            true,
            DateTimeOffset.Parse("2026-08-04T09:00:00Z"));
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, new[] { version }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var result = await client.GetAdministrativeActionWorkflowCatalogAsync();

        Assert.Equal(8, Assert.Single(result).Id);
        Assert.Equal(
            "/api/administrative-actions/workflows",
            Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task PrivilegedTaskContext_UsesDedicatedRouteWithoutOrdinaryTaskAccess()
    {
        var now = DateTimeOffset.Parse("2026-08-04T09:00:00Z");
        var version = new WorkflowSummaryDto(
            8,
            "Purchase request",
            "purchase-request",
            3,
            true,
            true,
            now);
        var context = new AdministrativeActionTaskContextDto(
            91,
            41,
            12,
            7,
            "Approval",
            "TASK_APPROVAL",
            8,
            "purchase-request",
            "Purchase request",
            3,
            now,
            now,
            [version]);
        using var handler = new RecordingHandler(Response(HttpStatusCode.OK, context));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var result = await client.GetAdministrativeActionTaskContextAsync(91);

        Assert.NotNull(result);
        Assert.Equal(41, result.InstanceId);
        Assert.Equal(8, Assert.Single(result.TargetVersions).Id);
        Assert.Equal(
            "/api/user-tasks/91/administrative-context",
            Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task ActionDiscoveryAndCandidateSearch_UseExactTargetVersionContracts()
    {
        var now = DateTimeOffset.Parse("2026-08-04T10:00:00Z");
        var action = new AdministrativeActionSummaryDto(
            14, "SEND_BACK_REVIEW", "Send back", 7, "Approval", 3, "Review", true, []);
        var candidate = new AdministrativeActionCandidateDto(
            91, 41, 12, 5, "purchase-request", "PR-41", 7, "Approval", "TASK_APPROVAL",
            now, now, true, []);
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, new[] { action }),
            Response(HttpStatusCode.OK, new PagedResult<AdministrativeActionCandidateDto>([candidate], 2, 25, 1)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var actions = await client.GetWorkflowAdministrativeActionsAsync(8, batchableOnly: true);
        var result = await client.SearchAdministrativeActionCandidatesAsync(new AdministrativeActionCandidateSearchRequest
        {
            TargetWorkflowId = 8,
            FlowExternalId = action.FlowExternalId,
            InstanceId = 41,
            BusinessKey = "PR-41",
            IncludeVariables = true,
            Page = 2,
            PageSize = 25
        });

        var listedAction = Assert.Single(actions);
        Assert.Equal(action.FlowExternalId, listedAction.FlowExternalId);
        Assert.Equal(action.SourceNodeId, listedAction.SourceNodeId);
        Assert.True(listedAction.IsBatchable);
        var listedCandidate = Assert.Single(result.Items);
        Assert.Equal(candidate.UserTaskId, listedCandidate.UserTaskId);
        Assert.Equal(candidate.InstanceId, listedCandidate.InstanceId);
        Assert.True(listedCandidate.Eligible);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/api/workflows/8/administrative-actions?batchableOnly=true", request.Path),
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/administrative-actions/candidates/search", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal(8, body.RootElement.GetProperty("targetWorkflowId").GetInt64());
                Assert.Equal("SEND_BACK_REVIEW", body.RootElement.GetProperty("flowExternalId").GetString());
                Assert.Equal(41, body.RootElement.GetProperty("instanceId").GetInt64());
                Assert.True(body.RootElement.GetProperty("includeVariables").GetBoolean());
            });
    }

    [Fact]
    public async Task CreateBatch_SerializesFrozenAllMatchingSelectionAndAuditInput()
    {
        var detail = BatchDetail(37, "preparing");
        using var handler = new RecordingHandler(Response(HttpStatusCode.Accepted, detail));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);
        var variable = JsonDocument.Parse("125").RootElement.Clone();
        var selectionSearch = new AdministrativeActionCandidateSearchRequest
        {
            TargetWorkflowId = 8,
            FlowExternalId = "SEND_BACK_REVIEW",
            SourceWorkflowId = 5,
            BusinessKey = "PR",
            Page = null,
            PageSize = null
        };

        var result = await client.CreateAdministrativeActionBatchAsync(new CreateAdministrativeActionBatchRequest(
            8,
            "SEND_BACK_REVIEW",
            "Policy correction",
            new Dictionary<string, JsonElement> { ["amount"] = variable },
            new AdministrativeActionBatchSelectionDto(
                AdministrativeActionBatchSelectionModes.AllMatching,
                null,
                selectionSearch,
                [91, 92]),
            "ui-retry-key"));

        Assert.Equal(37, result.Summary.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/administrative-action-batches", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("Policy correction", root.GetProperty("reason").GetString());
        Assert.Equal(125, root.GetProperty("variables").GetProperty("amount").GetInt32());
        Assert.Equal("allMatching", root.GetProperty("selection").GetProperty("mode").GetString());
        Assert.Equal(2, root.GetProperty("selection").GetProperty("excludedUserTaskIds").GetArrayLength());
        Assert.Equal(5, root.GetProperty("selection").GetProperty("allMatching").GetProperty("sourceWorkflowId").GetInt64());
        Assert.Equal("ui-retry-key", root.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task MonitorConfirmAndCancel_UseOptimisticBatchTimestampAndPagedRoutes()
    {
        var ready = BatchDetail(37, "ready", eligible: 4);
        var queued = BatchDetail(37, "queued", eligible: 4);
        var cancelled = BatchDetail(37, "cancelled", eligible: 4);
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, ready),
            Response(HttpStatusCode.OK, new PagedResult<AdministrativeActionBatchItemDto>([], 3, 50, 111)),
            Response(HttpStatusCode.OK, queued),
            Response(HttpStatusCode.OK, cancelled));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var loaded = await client.GetAdministrativeActionBatchAsync(37);
        var items = await client.GetAdministrativeActionBatchItemsAsync(37, "ineligible", 3, 50);
        await client.ConfirmAdministrativeActionBatchAsync(37,
            new ConfirmAdministrativeActionBatchRequest(4, ready.Summary.UpdatedAt));
        await client.CancelAdministrativeActionBatchAsync(37,
            new CancelAdministrativeActionBatchRequest("Stop remaining work"));

        Assert.NotNull(loaded);
        Assert.Equal(111, items.TotalCount);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/api/administrative-action-batches/37", request.Path),
            request => Assert.Equal("/api/administrative-action-batches/37/items?page=3&pageSize=50&status=ineligible", request.Path),
            request =>
            {
                Assert.Equal("/api/administrative-action-batches/37/confirm", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal(4, body.RootElement.GetProperty("expectedEligibleItemCount").GetInt32());
                Assert.Equal(ready.Summary.UpdatedAt, body.RootElement.GetProperty("expectedBatchUpdatedAt").GetDateTimeOffset());
            },
            request =>
            {
                Assert.Equal("/api/administrative-action-batches/37/cancel", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("Stop remaining work", body.RootElement.GetProperty("reason").GetString());
            });
    }

    private static AdministrativeActionBatchDetailDto BatchDetail(long id, string status, int eligible = 0)
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var summary = new AdministrativeActionBatchSummaryDto(
            id, 8, "purchase-request", "SEND_BACK_REVIEW", "Policy correction", status,
            "admin-user", null, 5, eligible, 5 - eligible, 0, 0, 0, 0, 0, now, now, null);
        return new AdministrativeActionBatchDetailDto(
            summary,
            new Dictionary<string, JsonElement>(),
            JsonDocument.Parse("{}").RootElement.Clone(),
            ["admin"],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static HttpResponseMessage Response<T>(HttpStatusCode statusCode, T content) => new(statusCode)
    {
        Content = JsonContent.Create(content)
    };

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int index;
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses[index++];
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);
}
