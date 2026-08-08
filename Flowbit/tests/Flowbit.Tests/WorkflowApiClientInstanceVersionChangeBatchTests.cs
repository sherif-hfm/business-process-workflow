extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using InstanceVersionChangesPage = FlowbitUi::Flowbit.Ui.Components.Pages.InstanceVersionChanges;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowApiClientInstanceVersionChangeBatchTests
{
    [Fact]
    public void SharedContractsExposeFrozenSelectionAndCorrelatedInstanceAudit()
    {
        Assert.Equal("explicit", InstanceVersionChangeBatchSelectionModes.Explicit);
        Assert.Equal("allMatching", InstanceVersionChangeBatchSelectionModes.AllMatching);
        Assert.Equal(typeof(long?), typeof(InstanceVersionChangeAuditDto).GetProperty("BatchId")?.PropertyType);
        Assert.Equal(typeof(long?), typeof(InstanceVersionChangeAuditDto).GetProperty("BatchItemId")?.PropertyType);
        Assert.Equal(typeof(int), typeof(InstanceVersionChangeBatchSummaryDto).GetProperty("StaleItemCount")?.PropertyType);
        Assert.Equal(typeof(int), typeof(InstanceVersionChangeBatchSummaryDto).GetProperty("BlockedItemCount")?.PropertyType);
        Assert.Equal(typeof(JsonElement?), typeof(InstanceVersionChangeBatchItemDto).GetProperty("Result")?.PropertyType);
        Assert.Equal(
            new[]
            {
                "ExpectedEligibleItemCount",
                "ExpectedIneligibleItemCount",
                "ExpectedWarningItemCount",
                "ExpectedBatchUpdatedAt"
            },
            typeof(ConfirmInstanceVersionChangeBatchRequest).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task CandidateSearchPostsExactSourceAndNestedFilterToBatchRoute()
    {
        using var variableFilterDocument = JsonDocument.Parse("{\"amount\":{\"$gte\":1000}}");
        using var handler = new RecordingHandler(Response(
            HttpStatusCode.OK,
            new PagedResult<InstanceVersionChangeCandidateDto>([], 2, 25, 73)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var result = await client.SearchInstanceVersionChangeCandidatesAsync(
            new InstanceVersionChangeCandidateSearchRequest
            {
                Filter = new InstanceVersionChangeCandidateFilterDto
                {
                    SourceWorkflowId = 17,
                    InstanceId = 41,
                    BusinessKey = "PR-41",
                    NodeId = 7,
                    NodeExternalId = "TASK_APPROVAL",
                    VariableFilter = variableFilterDocument.RootElement.Clone()
                },
                IncludeVariables = true,
                Page = 2,
                PageSize = 25
            });

        Assert.Equal(73, result.TotalCount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/instance-version-change-batches/candidates/search", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        var filter = root.GetProperty("filter");
        Assert.Equal(17, filter.GetProperty("sourceWorkflowId").GetInt64());
        Assert.Equal(41, filter.GetProperty("instanceId").GetInt64());
        Assert.Equal("PR-41", filter.GetProperty("businessKey").GetString());
        Assert.Equal(7, filter.GetProperty("nodeId").GetInt32());
        Assert.Equal("TASK_APPROVAL", filter.GetProperty("nodeExternalId").GetString());
        Assert.Equal(1000, filter.GetProperty("variableFilter").GetProperty("amount").GetProperty("$gte").GetInt32());
        Assert.True(root.GetProperty("includeVariables").GetBoolean());
        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(25, root.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task CreateBatchSerializesVersionPairReasonFrozenSelectionAndRetryKey()
    {
        var detail = BatchDetail(91, "preparing");
        using var handler = new RecordingHandler(Response(HttpStatusCode.Accepted, detail));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);
        var filter = new InstanceVersionChangeCandidateFilterDto
        {
            SourceWorkflowId = 17,
            BusinessKey = "PR",
            NodeExternalId = "TASK_APPROVAL"
        };

        var result = await client.CreateInstanceVersionChangeBatchAsync(
            new CreateInstanceVersionChangeBatchRequest(
                17,
                19,
                "Move the selected approval population",
                new InstanceVersionChangeBatchSelectionDto(
                    InstanceVersionChangeBatchSelectionModes.AllMatching,
                    null,
                    filter,
                    [41, 43]),
                "ui-retry-key"));

        Assert.Equal(91, result.Summary.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/instance-version-change-batches", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal(17, root.GetProperty("sourceWorkflowId").GetInt64());
        Assert.Equal(19, root.GetProperty("targetWorkflowId").GetInt64());
        Assert.Equal("Move the selected approval population", root.GetProperty("reason").GetString());
        Assert.Equal("allMatching", root.GetProperty("selection").GetProperty("mode").GetString());
        Assert.Equal(17, root.GetProperty("selection").GetProperty("filter").GetProperty("sourceWorkflowId").GetInt64());
        Assert.Equal([41L, 43L], root.GetProperty("selection").GetProperty("excludedInstanceIds").EnumerateArray().Select(item => item.GetInt64()));
        Assert.Equal("ui-retry-key", root.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task MonitorListConfirmAndCancelUsePagedRoutesAndDisplayedConcurrencyFence()
    {
        var ready = BatchDetail(91, "ready", eligible: 3, warnings: 1, ineligible: 2);
        var queued = BatchDetail(91, "queued", eligible: 3, warnings: 1, ineligible: 2);
        var cancelled = BatchDetail(91, "cancelled", eligible: 3, warnings: 1, ineligible: 2);
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, ready),
            Response(HttpStatusCode.OK, new PagedResult<InstanceVersionChangeBatchItemDto>([], 3, 50, 111)),
            Response(HttpStatusCode.OK, new PagedResult<InstanceVersionChangeBatchSummaryDto>([], 2, 25, 12)),
            Response(HttpStatusCode.OK, queued),
            Response(HttpStatusCode.OK, cancelled));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var loaded = await client.GetInstanceVersionChangeBatchAsync(91);
        var items = await client.GetInstanceVersionChangeBatchItemsAsync(91, "ineligible", 3, 50);
        var batches = await client.GetInstanceVersionChangeBatchesAsync(new InstanceVersionChangeBatchSearchRequest
        {
            WorkflowKey = "purchase-request",
            SourceWorkflowId = 17,
            TargetWorkflowId = 19,
            Status = "ready",
            PreparedBy = "operator",
            Page = 2,
            PageSize = 25
        });
        await client.ConfirmInstanceVersionChangeBatchAsync(91,
            new ConfirmInstanceVersionChangeBatchRequest(3, 2, 1, ready.Summary.UpdatedAt));
        await client.CancelInstanceVersionChangeBatchAsync(91,
            new CancelInstanceVersionChangeBatchRequest("Stop remaining changes"));

        Assert.NotNull(loaded);
        Assert.Equal(111, items.TotalCount);
        Assert.Equal(12, batches.TotalCount);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/api/instance-version-change-batches/91", request.Path),
            request => Assert.Equal("/api/instance-version-change-batches/91/items?page=3&pageSize=50&status=ineligible", request.Path),
            request => Assert.Equal("/api/instance-version-change-batches?page=2&pageSize=25&workflowKey=purchase-request&sourceWorkflowId=17&targetWorkflowId=19&status=ready&preparedBy=operator", request.Path),
            request =>
            {
                Assert.Equal("/api/instance-version-change-batches/91/confirm", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal(3, body.RootElement.GetProperty("expectedEligibleItemCount").GetInt32());
                Assert.Equal(2, body.RootElement.GetProperty("expectedIneligibleItemCount").GetInt32());
                Assert.Equal(1, body.RootElement.GetProperty("expectedWarningItemCount").GetInt32());
                Assert.Equal(ready.Summary.UpdatedAt, body.RootElement.GetProperty("expectedBatchUpdatedAt").GetDateTimeOffset());
            },
            request =>
            {
                Assert.Equal("/api/instance-version-change-batches/91/cancel", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("Stop remaining changes", body.RootElement.GetProperty("reason").GetString());
            });
    }

    [Fact]
    public async Task MissingBatchReturnsNullWithoutTreatingNotFoundAsTransportFailure()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var result = await client.GetInstanceVersionChangeBatchAsync(404);

        Assert.Null(result);
        Assert.Equal("/api/instance-version-change-batches/404", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public void PageStateKeepsCrossPageSelectionAndCountsUnicodeReason()
    {
        var page = new InstanceVersionChangesPage();
        SetField(page, "candidates", new PagedResult<InstanceVersionChangeCandidateDto>([], 1, 50, 3));

        Invoke(page, "SetSelectionMode", true);
        Invoke(page, "ToggleCandidate", 42L);

        Assert.Equal(2L, GetProperty<long>(page, "SelectedCandidateCount"));
        Assert.Contains(42L, GetField<HashSet<long>>(page, "excludedInstances"));

        Invoke(page, "SetSelectionMode", false);
        Invoke(page, "ToggleCandidate", 91L);

        SetField(page, "reason", "🚀");
        Assert.Equal(1, GetProperty<int>(page, "ReasonLength"));
        Assert.True(GetProperty<bool>(page, "HasValidReason"));
    }

    [Fact]
    public async Task EveryCandidateFilterInputInvalidatesTheAppliedSearchAndSelection()
    {
        var page = new InstanceVersionChangesPage();
        SetField(page, "candidates", new PagedResult<InstanceVersionChangeCandidateDto>([], 1, 50, 3));

        AssertInvalidated(
            page,
            "OnCandidateInstanceIdInput",
            new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "34" },
            "candidateInstanceId",
            34L);
        AssertInvalidated(
            page,
            "OnCandidateBusinessKeyInput",
            new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "PR-1042" },
            "candidateBusinessKey",
            "PR-1042");
        AssertInvalidated(
            page,
            "OnCandidateNodeIdInput",
            new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "not-a-number" },
            "candidateNodeId",
            (int?)null);
        AssertInvalidated(
            page,
            "OnCandidateNodeExternalIdInput",
            new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "manager-review" },
            "candidateNodeExternalId",
            "manager-review");

        ResetAppliedCandidateSelection(page);
        var variableFilterChange = Assert.IsAssignableFrom<Task>(Invoke(
            page,
            "OnCandidateVariableFilterChanged",
            "{\"amount\":{\"$gt\":1000}}"));
        await variableFilterChange;
        Assert.Equal(
            "{\"amount\":{\"$gt\":1000}}",
            GetField<string?>(page, "candidateVariableFilterJson"));
        AssertCandidateSearchInvalidated(page);

        AssertInvalidated(
            page,
            "OnIncludeVariablesChanged",
            new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true },
            "includeVariables",
            true);
    }

    private static InstanceVersionChangeBatchDetailDto BatchDetail(
        long id,
        string status,
        int eligible = 0,
        int warnings = 0,
        int ineligible = 5)
    {
        var now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        var source = Workflow(17, 2, now.AddDays(-2));
        var target = Workflow(19, 4, now.AddDays(-1));
        var summary = new InstanceVersionChangeBatchSummaryDto(
            id,
            source,
            target,
            InstanceVersionChangeDirections.Upgrade,
            "Move the selected approval population",
            status,
            "operator",
            null,
            eligible + ineligible,
            eligible,
            warnings,
            0,
            ineligible,
            ineligible,
            0,
            0,
            0,
            0,
            0,
            now,
            now,
            null);
        return new InstanceVersionChangeBatchDetailDto(
            summary,
            JsonDocument.Parse("{}").RootElement.Clone(),
            ["admin"],
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            null,
            null,
            null);
    }

    private static WorkflowSummaryDto Workflow(long id, int version, DateTimeOffset createdAt) =>
        new(id, "Purchase request", "purchase-request", version, true, false, createdAt);

    private static HttpResponseMessage Response<T>(HttpStatusCode statusCode, T content) => new(statusCode)
    {
        Content = JsonContent.Create(content)
    };

    private static object? Invoke(object target, string name, params object?[] arguments) =>
        Method(target, name).Invoke(target, arguments);

    private static void AssertInvalidated<T>(
        InstanceVersionChangesPage page,
        string handler,
        Microsoft.AspNetCore.Components.ChangeEventArgs args,
        string field,
        T expected)
    {
        ResetAppliedCandidateSelection(page);
        Invoke(page, handler, args);
        Assert.Equal(expected, GetField<T>(page, field));
        AssertCandidateSearchInvalidated(page);
    }

    private static void ResetAppliedCandidateSelection(InstanceVersionChangesPage page)
    {
        SetField(page, "candidateFiltersDirty", false);
        SetField(page, "candidatePage", 4);
        GetField<HashSet<long>>(page, "explicitlySelectedInstances").Add(91);
        GetField<HashSet<long>>(page, "excludedInstances").Add(42);
    }

    private static void AssertCandidateSearchInvalidated(InstanceVersionChangesPage page)
    {
        Assert.True(GetField<bool>(page, "candidateFiltersDirty"));
        Assert.Equal(1, GetField<int>(page, "candidatePage"));
        Assert.Empty(GetField<HashSet<long>>(page, "explicitlySelectedInstances"));
        Assert.Empty(GetField<HashSet<long>>(page, "excludedInstances"));
    }

    private static MethodInfo Method(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Method '{name}' was not found on {target.GetType().Name}.");

    private static T GetField<T>(object target, string name) =>
        (T)(Field(target, name).GetValue(target)
            ?? (default(T) is null ? default! : throw new InvalidOperationException($"Field '{name}' is null.")));

    private static void SetField(object target, string name, object? value) =>
        Field(target, name).SetValue(target, value);

    private static FieldInfo Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Field '{name}' was not found on {target.GetType().Name}.");

    private static T GetProperty<T>(object target, string name) =>
        (T)(target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new InvalidOperationException($"Property '{name}' was not found on {target.GetType().Name}."));

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
