extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using InstanceVariableUpdatesPage = FlowbitUi::Flowbit.Ui.Components.Pages.InstanceVariableUpdates;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowApiClientInstanceVariableUpdateTests
{
    [Fact]
    public async Task DirectUpdateUsesPatchAndPreservesRawJsonKinds()
    {
        using var objectDocument = JsonDocument.Parse("{\"approved\":true}");
        using var nullDocument = JsonDocument.Parse("null");
        var result = new UpdateInstanceVariablesResultDto(
            81,
            42,
            17,
            DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
            [new InstanceVariableUpdateOutcomeDto("context", "added", 991, objectDocument.RootElement.Clone())],
            []);
        using var handler = new RecordingHandler(Response(HttpStatusCode.OK, result));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var loaded = await client.UpdateInstanceVariablesAsync(
            42,
            new UpdateInstanceVariablesRequest(
                [
                    new InstanceVariableWriteDto("context", objectDocument.RootElement.Clone()),
                    new InstanceVariableWriteDto("cleared", nullDocument.RootElement.Clone())
                ],
                "Correct imported state",
                "direct-retry"));

        Assert.Equal(81, loaded.OperationId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/instances/42/variables", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.True(body.RootElement.GetProperty("variables")[0].GetProperty("value").GetProperty("approved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("variables")[1].GetProperty("value").ValueKind);
        Assert.Equal("Correct imported state", body.RootElement.GetProperty("reason").GetString());
        Assert.Equal("direct-retry", body.RootElement.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task DirectUpdateSurfacesDomainConflictDescription()
    {
        using var valueDocument = JsonDocument.Parse("1");
        using var handler = new RecordingHandler(Response(
            HttpStatusCode.Conflict,
            new { error = new { code = "instance_not_running", description = "Only running instances can be updated." } }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var exception = await Assert.ThrowsAsync<WorkflowApiException>(() => client.UpdateInstanceVariablesAsync(
            42,
            new UpdateInstanceVariablesRequest(
                [new InstanceVariableWriteDto("priority", valueDocument.RootElement.Clone())],
                null,
                null)));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("Only running instances can be updated.", exception.Message);
    }

    [Fact]
    public async Task CandidateSearchPostsFamilyVersionAdvancedFilterSortAndCursor()
    {
        using var filterDocument = JsonDocument.Parse("{\"amount\":{\"$gte\":1000}}");
        using var handler = new RecordingHandler(Response(
            HttpStatusCode.OK,
            new PagedResult<InstanceVariableUpdateCandidateDto>([], 2, 25, 73) { NextCursor = "next" }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var loaded = await client.SearchInstanceVariableUpdateCandidatesAsync(new InstanceVariableUpdateCandidateSearchRequest
        {
            Filter = new InstanceVariableUpdateCandidateFilterDto
            {
                WorkflowKey = "purchase-request",
                WorkflowId = 17,
                InstanceId = 41,
                BusinessKey = "PR-41",
                NodeExternalId = "TASK_APPROVAL",
                VariableFilter = filterDocument.RootElement.Clone()
            },
            IncludeVariables = true,
            Sort = [new SearchSortDto("updatedAt", "desc")],
            Cursor = "previous-page-fence",
            Page = 2,
            PageSize = 25
        });

        Assert.Equal("next", loaded.NextCursor);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/instance-variable-update-batches/candidates/search", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("purchase-request", root.GetProperty("filter").GetProperty("workflowKey").GetString());
        Assert.Equal(17, root.GetProperty("filter").GetProperty("workflowId").GetInt64());
        Assert.Equal(1000, root.GetProperty("filter").GetProperty("variableFilter").GetProperty("amount").GetProperty("$gte").GetInt32());
        Assert.Equal("updatedAt", root.GetProperty("sort")[0].GetProperty("field").GetString());
        Assert.Equal("previous-page-fence", root.GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task CreateConfirmCancelAndHistoryUseDurableBatchRoutes()
    {
        using var valueDocument = JsonDocument.Parse("[\"admin\"]");
        var ready = BatchDetail(91, "ready", eligible: 3, warnings: 1, ineligible: 2);
        var queued = BatchDetail(91, "queued", eligible: 3, warnings: 1, ineligible: 2);
        var cancelled = BatchDetail(91, "cancelled", eligible: 3, warnings: 1, ineligible: 2);
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.Accepted, ready),
            Response(HttpStatusCode.OK, new PagedResult<InstanceVariableUpdateBatchSummaryDto>([], 2, 25, 12)),
            Response(HttpStatusCode.OK, ready),
            Response(HttpStatusCode.OK, new PagedResult<InstanceVariableUpdateBatchItemDto>([], 3, 50, 111)),
            Response(HttpStatusCode.OK, queued),
            Response(HttpStatusCode.OK, cancelled));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        await client.CreateInstanceVariableUpdateBatchAsync(new CreateInstanceVariableUpdateBatchRequest(
            "purchase-request",
            [new InstanceVariableWriteDto("reviewers", valueDocument.RootElement.Clone())],
            null,
            new InstanceVariableUpdateBatchSelectionDto(
                InstanceVariableUpdateBatchSelectionModes.AllMatching,
                null,
                new InstanceVariableUpdateCandidateFilterDto { WorkflowKey = "purchase-request", WorkflowId = 17 },
                [41, 43]),
            "batch-retry"));
        await client.GetInstanceVariableUpdateBatchesAsync(new InstanceVariableUpdateBatchSearchRequest
        {
            WorkflowKey = "purchase-request",
            Status = "ready",
            PreparedBy = "operator",
            Page = 2,
            PageSize = 25
        });
        await client.GetInstanceVariableUpdateBatchAsync(91);
        await client.GetInstanceVariableUpdateBatchItemsAsync(91, "eligible", 3, 50);
        await client.ConfirmInstanceVariableUpdateBatchAsync(91, new ConfirmInstanceVariableUpdateBatchRequest(3, 2, 1, ready.Summary.UpdatedAt));
        await client.CancelInstanceVariableUpdateBatchAsync(91, new CancelInstanceVariableUpdateBatchRequest("Stop remaining updates"));

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/instance-variable-update-batches", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("allMatching", body.RootElement.GetProperty("selection").GetProperty("mode").GetString());
                Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("variables")[0].GetProperty("value").ValueKind);
            },
            request => Assert.Equal("/api/instance-variable-update-batches?page=2&pageSize=25&workflowKey=purchase-request&status=ready&preparedBy=operator", request.Path),
            request => Assert.Equal("/api/instance-variable-update-batches/91", request.Path),
            request => Assert.Equal("/api/instance-variable-update-batches/91/items?page=3&pageSize=50&status=eligible", request.Path),
            request =>
            {
                Assert.Equal("/api/instance-variable-update-batches/91/confirm", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal(3, body.RootElement.GetProperty("expectedEligibleItemCount").GetInt32());
                Assert.Equal(ready.Summary.UpdatedAt, body.RootElement.GetProperty("expectedBatchUpdatedAt").GetDateTimeOffset());
            },
            request =>
            {
                Assert.Equal("/api/instance-variable-update-batches/91/cancel", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("Stop remaining updates", body.RootElement.GetProperty("reason").GetString());
            });
    }

    [Fact]
    public void PageStateKeepsCrossPageSelectionForBothModes()
    {
        var page = new InstanceVariableUpdatesPage();
        SetField(page, "candidates", new PagedResult<InstanceVariableUpdateCandidateDto>([], 1, 50, 5));

        Invoke(page, "SetSelectionMode", false);
        Invoke(page, "ToggleCandidate", 11L);
        Invoke(page, "ToggleCandidate", 22L);
        Assert.Equal(2L, GetProperty<long>(page, "SelectedCandidateCount"));
        Assert.Equal([11L, 22L], GetField<HashSet<long>>(page, "explicitlySelectedInstances").Order());

        Invoke(page, "SetSelectionMode", true);
        Invoke(page, "ToggleCandidate", 33L);
        Invoke(page, "ToggleCandidate", 44L);
        Assert.Equal(3L, GetProperty<long>(page, "SelectedCandidateCount"));
        Assert.Equal([33L, 44L], GetField<HashSet<long>>(page, "excludedInstances").Order());
        Assert.Empty(GetField<HashSet<long>>(page, "explicitlySelectedInstances"));

        Invoke(page, "ToggleCandidate", 33L);
        Assert.Equal(4L, GetProperty<long>(page, "SelectedCandidateCount"));
        Assert.Equal([44L], GetField<HashSet<long>>(page, "excludedInstances"));
    }

    [Fact]
    public void ChangingCandidateFiltersInvalidatesPagingAndSelection()
    {
        var page = new InstanceVariableUpdatesPage();
        SetField(page, "candidates", new PagedResult<InstanceVariableUpdateCandidateDto>([], 4, 50, 203));
        SetField(page, "appliedCandidateFilter", new InstanceVariableUpdateCandidateFilterDto { WorkflowKey = "purchase-request" });
        SetField(page, "candidatePage", 4);
        GetField<List<string?>>(page, "candidatePageCursors").AddRange(["page-2", "page-3", "page-4"]);
        Invoke(page, "SetSelectionMode", true);
        Invoke(page, "ToggleCandidate", 91L);
        SetField(page, "draftIdempotencyKey", "retry-key");
        var oldVersion = GetField<long>(page, "candidateRequestVersion");

        Invoke(
            page,
            "OnCandidateBusinessKeyInput",
            new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "PR-1042" });

        Assert.True(GetField<bool>(page, "candidateFiltersDirty"));
        Assert.Equal(1, GetField<int>(page, "candidatePage"));
        Assert.Equal([null], GetField<List<string?>>(page, "candidatePageCursors"));
        Assert.Empty(GetField<HashSet<long>>(page, "explicitlySelectedInstances"));
        Assert.Empty(GetField<HashSet<long>>(page, "excludedInstances"));
        Assert.Null(GetField<string?>(page, "draftIdempotencyKey"));
        Assert.True(GetField<long>(page, "candidateRequestVersion") > oldVersion);
        Assert.False(GetProperty<bool>(page, "CanCreateBatch"));
    }

    [Fact]
    public async Task QueryBatchIdReopensBatchAndDisposalCancelsPolling()
    {
        var detail = BatchDetail(91, "ready", eligible: 3, warnings: 1, ineligible: 2);
        using var handler = new VariableUpdatePageHandler(detail);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var page = new InstanceVariableUpdatesPage { RequestedBatchId = 91 };
        var token = new TokenState();
        token.Set("test-token");
        SetProperty(page, "Api", new WorkflowApiClient(http));
        SetProperty(page, "Token", token);

        await InvokeAsync(page, "OnInitializedAsync");

        Assert.Equal(91, GetField<InstanceVariableUpdateBatchDetailDto?>(page, "currentBatch")?.Summary.Id);
        Assert.NotNull(GetField<PagedResult<InstanceVariableUpdateBatchItemDto>?>(page, "batchItems"));
        Assert.Contains("/api/instance-variable-update-batches/91", handler.Paths);
        Assert.Contains("/api/instance-variable-update-batches/91/items?page=1&pageSize=50", handler.Paths);

        var cancellation = GetField<CancellationTokenSource>(page, "pollCancellation").Token;
        var pollTask = GetField<Task?>(page, "pollTask");
        Assert.NotNull(pollTask);

        await page.DisposeAsync();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(pollTask.IsCompletedSuccessfully);
    }

    private static InstanceVariableUpdateBatchDetailDto BatchDetail(long id, string status, int eligible, int warnings, int ineligible)
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        return new InstanceVariableUpdateBatchDetailDto(
            new InstanceVariableUpdateBatchSummaryDto(
                id, "purchase-request", status, "operator", null, null, 1, 1,
                eligible + ineligible, eligible, ineligible, warnings, 0, 0, 0, 0, 0,
                now, now, null),
            JsonSerializer.SerializeToElement(new { mode = "explicit" }),
            [], ["admin"], null, [], [], null, null, now, null, null, null);
    }

    private static HttpResponseMessage Response<T>(HttpStatusCode statusCode, T content) => new(statusCode)
    {
        Content = JsonContent.Create(content)
    };

    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{name}' was not found.");
        return method.Invoke(target, arguments);
    }

    private static async Task InvokeAsync(object target, string name, params object?[] arguments)
    {
        var task = Assert.IsAssignableFrom<Task>(Invoke(target, name, arguments));
        await task;
    }

    private static T GetField<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{name}' was not found.");
        return (T)(field.GetValue(target)
            ?? (default(T) is null ? default! : throw new InvalidOperationException($"Field '{name}' is null.")));
    }

    private static void SetField(object target, string name, object? value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{name}' was not found.")).SetValue(target, value);

    private static void SetProperty(object target, string name, object value) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{name}' was not found.")).SetValue(target, value);

    private static T GetProperty<T>(object target, string name) =>
        (T)(target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new InvalidOperationException($"Property '{name}' was not found."));

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int index;
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri?.PathAndQuery ?? string.Empty, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses[index++];
        }
    }

    private sealed class VariableUpdatePageHandler(InstanceVariableUpdateBatchDetailDto detail) : HttpMessageHandler
    {
        private readonly WorkflowSummaryDto workflow = new(
            17,
            "Purchase request",
            "purchase-request",
            2,
            true,
            false,
            DateTimeOffset.Parse("2026-08-01T12:00:00Z"));

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            Paths.Add(path);
            HttpResponseMessage response = path switch
            {
                "/api/workflows" => Response(HttpStatusCode.OK, new[] { workflow }),
                "/api/workflows/purchase-request/versions" => Response(HttpStatusCode.OK, new[] { workflow }),
                "/api/instance-variable-update-batches?page=1&pageSize=25" => Response(
                    HttpStatusCode.OK,
                    new PagedResult<InstanceVariableUpdateBatchSummaryDto>([detail.Summary], 1, 25, 1)),
                "/api/instance-variable-update-batches/91" => Response(HttpStatusCode.OK, detail),
                "/api/instance-variable-update-batches/91/items?page=1&pageSize=50" => Response(
                    HttpStatusCode.OK,
                    new PagedResult<InstanceVariableUpdateBatchItemDto>([], 1, 50, 0)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);
}
