extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
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
    public async Task OrdinaryTaskAuthorizationFailureUsesWorkflowApiException()
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
    public async Task AdministrativeWorkflowCatalogUsesAuthenticatedCatalogRoute()
    {
        var version = new WorkflowSummaryDto(
            8, "Purchase request", "purchase-request", 3, true, true,
            DateTimeOffset.Parse("2026-08-04T09:00:00Z"));
        using var handler = new RecordingHandler(Response(HttpStatusCode.OK, new[] { version }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var result = await client.GetAdministrativeActionWorkflowCatalogAsync();

        Assert.Equal(8, Assert.Single(result).Id);
        Assert.Equal("/api/administrative-actions/workflows", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task NodeActionDiscoveryAndCandidateSearchUseExactDefinitionNodeAndPosition()
    {
        var now = DateTimeOffset.Parse("2026-08-04T10:00:00Z");
        var activationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var node = new AdministrativeActionSourceNodeDto(8, 3, 7, "Approval", "TASK_APPROVAL", true);
        var action = new AdministrativeActionSummaryDto(
            8, 3, AdministrativeActionKinds.TimerBoundary, 27, null, "Escalate now",
            7, "Approval", 9, "Escalation", BpmnFlowNodeTypes.UserTask, [])
        {
            BoundaryNodeId = 8,
            BoundaryNodeName = "Approval timeout",
            Timer = new TimerDefinitionModel { TimeDuration = "PT4H" },
            AuthoredCancelActivity = false,
            Condition = "amount > 1000",
            Roles = ["supervisor"]
        };
        var candidate = new AdministrativeActionCandidateDto(
            AdministrativeActionPositionKinds.MultiInstanceExecution,
            73,
            null,
            73,
            41,
            12,
            activationId,
            8,
            3,
            "purchase-request",
            "PR-41",
            7,
            "Approval",
            "TASK_APPROVAL",
            now,
            6,
            [new AdministrativeTimerBoundaryStateDto(8, 501, 601, "paused", now.AddHours(2), 1, now, true)]);
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, new[] { node }),
            Response(HttpStatusCode.OK, new[] { action }),
            Response(HttpStatusCode.OK, new PagedResult<AdministrativeActionCandidateDto>([candidate], 2, 25, 1)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var nodes = await client.GetWorkflowAdministrativeActionNodesAsync(8);
        var actions = await client.GetWorkflowAdministrativeActionsAsync(8, 7);
        var result = await client.SearchAdministrativeActionCandidatesAsync(new AdministrativeActionCandidateSearchRequest
        {
            WorkflowDefinitionId = 8,
            SourceNodeId = 7,
            PositionKind = AdministrativeActionPositionKinds.MultiInstanceExecution,
            PositionId = 73,
            InstanceId = 41,
            BusinessKey = "PR-41",
            IncludeVariables = true,
            Page = 2,
            PageSize = 25
        });

        Assert.True(Assert.Single(nodes).IsMultiInstance);
        Assert.Equal(8, Assert.Single(actions).BoundaryNodeId);
        Assert.Equal(73, Assert.Single(result.Items).PositionId);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/api/workflows/8/administrative-actions/nodes", request.Path),
            request => Assert.Equal("/api/workflows/8/nodes/7/administrative-actions", request.Path),
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/administrative-actions/candidates/search", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal(8, body.RootElement.GetProperty("workflowDefinitionId").GetInt64());
                Assert.Equal(7, body.RootElement.GetProperty("sourceNodeId").GetInt32());
                Assert.Equal("multiInstanceExecution", body.RootElement.GetProperty("positionKind").GetString());
                Assert.Equal(73, body.RootElement.GetProperty("positionId").GetInt64());
                Assert.False(body.RootElement.TryGetProperty("flowMappings", out _));
            });
    }

    [Fact]
    public async Task CreateBatchSerializesActionCompositePositionsModeAndOptionalAuditInput()
    {
        var detail = BatchDetail(37, "preparing");
        using var handler = new RecordingHandler(Response(HttpStatusCode.Accepted, detail));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);
        var variable = JsonDocument.Parse("125").RootElement.Clone();
        var selectionSearch = new AdministrativeActionCandidateSearchRequest
        {
            WorkflowDefinitionId = 8,
            SourceNodeId = 7,
            BusinessKey = "PR",
            Page = null,
            PageSize = null
        };

        var result = await client.CreateAdministrativeActionBatchAsync(new CreateAdministrativeActionBatchRequest(
            8,
            7,
            AdministrativeActionKinds.DirectFlow,
            14,
            null,
            AdministrativeActionMultiInstanceModes.CompleteAllChildren,
            null,
            new Dictionary<string, JsonElement> { ["amount"] = variable },
            new AdministrativeActionBatchSelectionDto(
                AdministrativeActionBatchSelectionModes.AllMatching,
                null,
                selectionSearch,
                [new AdministrativeActionPositionReferenceDto(AdministrativeActionPositionKinds.UserTask, 91)]),
            "ui-retry-key"));

        Assert.Equal(37, result.Summary.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/administrative-action-batches", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal(8, root.GetProperty("workflowDefinitionId").GetInt64());
        Assert.Equal(7, root.GetProperty("sourceNodeId").GetInt32());
        Assert.Equal("directFlow", root.GetProperty("actionKind").GetString());
        Assert.Equal(14, root.GetProperty("flowId").GetInt32());
        Assert.Equal("completeAllChildren", root.GetProperty("multiInstanceMode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reason").ValueKind);
        Assert.Equal(125, root.GetProperty("variables").GetProperty("amount").GetInt32());
        Assert.Equal("allMatching", root.GetProperty("selection").GetProperty("mode").GetString());
        var excluded = Assert.Single(root.GetProperty("selection").GetProperty("excludedPositions").EnumerateArray());
        Assert.Equal("userTask", excluded.GetProperty("positionKind").GetString());
        Assert.Equal(91, excluded.GetProperty("positionId").GetInt64());
        Assert.Equal("ui-retry-key", root.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task MonitorConfirmCancelAndListUseAffectedCountTimestampAndPagedRoutes()
    {
        var ready = BatchDetail(37, "ready", eligible: 4, affected: 19);
        var queued = BatchDetail(37, "queued", eligible: 4, affected: 19);
        var cancelled = BatchDetail(37, "cancelled", eligible: 4, affected: 19);
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, ready),
            Response(HttpStatusCode.OK, new PagedResult<AdministrativeActionBatchItemDto>([], 3, 50, 111)),
            Response(HttpStatusCode.OK, new PagedResult<AdministrativeActionBatchSummaryDto>([], 1, 25, 0)),
            Response(HttpStatusCode.OK, queued),
            Response(HttpStatusCode.OK, cancelled));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var loaded = await client.GetAdministrativeActionBatchAsync(37);
        var items = await client.GetAdministrativeActionBatchItemsAsync(37, "ineligible", 3, 50);
        await client.GetAdministrativeActionBatchesAsync(new AdministrativeActionBatchSearchRequest
        {
            WorkflowDefinitionId = 8,
            Page = 1,
            PageSize = 25
        });
        await client.ConfirmAdministrativeActionBatchAsync(37,
            new ConfirmAdministrativeActionBatchRequest(4, 19, ready.Summary.UpdatedAt));
        await client.CancelAdministrativeActionBatchAsync(37,
            new CancelAdministrativeActionBatchRequest("Stop remaining work"));

        Assert.NotNull(loaded);
        Assert.Equal(111, items.TotalCount);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/api/administrative-action-batches/37", request.Path),
            request => Assert.Equal("/api/administrative-action-batches/37/items?page=3&pageSize=50&status=ineligible", request.Path),
            request => Assert.Equal("/api/administrative-action-batches?page=1&pageSize=25&workflowDefinitionId=8", request.Path),
            request =>
            {
                Assert.Equal("/api/administrative-action-batches/37/confirm", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal(4, body.RootElement.GetProperty("expectedEligibleItemCount").GetInt32());
                Assert.Equal(19, body.RootElement.GetProperty("expectedAffectedTaskCount").GetInt32());
                Assert.Equal(ready.Summary.UpdatedAt, body.RootElement.GetProperty("expectedBatchUpdatedAt").GetDateTimeOffset());
            },
            request =>
            {
                Assert.Equal("/api/administrative-action-batches/37/cancel", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("Stop remaining work", body.RootElement.GetProperty("reason").GetString());
            });
    }

    private static AdministrativeActionBatchDetailDto BatchDetail(
        long id,
        string status,
        int eligible = 0,
        int affected = 5)
    {
        var now = DateTimeOffset.Parse("2026-08-04T12:00:00Z");
        var summary = new AdministrativeActionBatchSummaryDto(
            id,
            "purchase-request",
            8,
            3,
            7,
            "Approval",
            AdministrativeActionKinds.DirectFlow,
            14,
            null,
            AdministrativeActionMultiInstanceModes.ForceParent,
            null,
            status,
            "operator",
            null,
            5,
            affected,
            eligible,
            5 - eligible,
            0,
            0,
            0,
            0,
            0,
            now,
            now,
            null);
        var action = new AdministrativeActionSummaryDto(
            8, 3, AdministrativeActionKinds.DirectFlow, 14, "SEND_BACK_REVIEW", "Send back",
            7, "Approval", 3, "Review", BpmnFlowNodeTypes.UserTask, []);
        return new AdministrativeActionBatchDetailDto(
            summary,
            action,
            new Dictionary<string, JsonElement>(),
            JsonDocument.Parse("{}").RootElement.Clone(),
            [],
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
