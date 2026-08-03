extern alias FlowbitUi;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowApiClientSettingsTests
{
    [Fact]
    public async Task EngineSettings_GetAndCreate_UseExpectedContractsAndDeserializeResponses()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
        var updatedAt = DateTimeOffset.Parse("2026-08-03T09:15:00Z");
        var existing = new EngineSettingDto(
            11,
            "Workflow",
            "AutomaticHopLimit",
            "250",
            "Maximum automatic hops.",
            createdAt,
            updatedAt);
        var created = new EngineSettingDto(
            12,
            null,
            "Settings.RequiredRole",
            "admin,ops",
            "Roles permitted to manage settings.",
            updatedAt,
            updatedAt);
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, new[] { existing }),
            Response(HttpStatusCode.Created, created));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var listed = await client.GetEngineSettingsAsync();
        var createdResult = await client.CreateEngineSettingAsync(new CreateEngineSettingRequest(
            null,
            "Settings.RequiredRole",
            "admin,ops",
            "Roles permitted to manage settings."));

        var listedSetting = Assert.Single(listed);
        Assert.Equal(existing, listedSetting);
        Assert.Equal(created, createdResult);

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/api/engine-settings", request.Path);
                Assert.Null(request.Body);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/engine-settings", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                var root = body.RootElement;
                Assert.Equal(JsonValueKind.Null, root.GetProperty("namespace").ValueKind);
                Assert.Equal("Settings.RequiredRole", root.GetProperty("key").GetString());
                Assert.Equal("admin,ops", root.GetProperty("value").GetString());
                Assert.Equal(
                    "Roles permitted to manage settings.",
                    root.GetProperty("description").GetString());
            });
    }

    [Fact]
    public async Task EngineSettings_UpdateAndDelete_SendConcurrencyAndEncodedTimestamp()
    {
        var expectedUpdatedAt = DateTimeOffset.Parse("2026-08-03T12:34:56.789+03:00");
        var updated = new EngineSettingDto(
            27,
            "Authentication",
            "UserIdentityClaim",
            "preferred_username",
            "JWT claim used as the actor identity.",
            expectedUpdatedAt.AddDays(-2),
            expectedUpdatedAt.AddMinutes(1));
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, updated),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var result = await client.UpdateEngineSettingAsync(
            27,
            new UpdateEngineSettingRequest(
                "preferred_username",
                "JWT claim used as the actor identity.",
                expectedUpdatedAt));
        await client.DeleteEngineSettingAsync(27, expectedUpdatedAt);

        Assert.Equal(updated, result);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/api/engine-settings/27", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                var root = body.RootElement;
                Assert.Equal("preferred_username", root.GetProperty("value").GetString());
                Assert.Equal(
                    "JWT claim used as the actor identity.",
                    root.GetProperty("description").GetString());
                Assert.Equal(
                    expectedUpdatedAt,
                    root.GetProperty("expectedUpdatedAt").GetDateTimeOffset());
                Assert.False(root.TryGetProperty("key", out _));
                Assert.False(root.TryGetProperty("namespace", out _));
            },
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                var timestamp = Uri.EscapeDataString(
                    expectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
                Assert.Equal(
                    $"/api/engine-settings/27?expectedUpdatedAt={timestamp}",
                    request.Path);
                Assert.Null(request.Body);
            });
    }

    [Fact]
    public async Task WorkflowSettings_GetAndCreate_PreserveArbitraryJsonAndDeserializeResponses()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var updatedAt = DateTimeOffset.Parse("2026-08-03T11:30:00Z");
        var existingValue = Json("""{"enabled":true,"limits":[1,2],"fallback":null}""");
        var createValue = Json("""{"roles":["ops","admin"],"threshold":7.5,"nested":{"enabled":false}}""");
        var existing = new WorkflowSettingDto(
            31,
            "examples",
            "catalog",
            existingValue,
            "Example catalog configuration.",
            createdAt,
            updatedAt);
        var created = new WorkflowSettingDto(
            32,
            "routing",
            "policy",
            createValue,
            "Typed routing policy.",
            updatedAt,
            updatedAt);
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, new[] { existing }),
            Response(HttpStatusCode.Created, created));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var listed = await client.GetWorkflowSettingsAsync();
        var createdResult = await client.CreateWorkflowSettingAsync(new CreateWorkflowSettingRequest(
            "routing",
            "policy",
            createValue,
            "Typed routing policy."));

        var listedSetting = Assert.Single(listed);
        Assert.Equal("Example catalog configuration.", listedSetting.Description);
        Assert.Equal(existingValue.GetRawText(), listedSetting.Value.GetRawText());
        Assert.Equal(32, createdResult.Id);
        Assert.Equal(createValue.GetRawText(), createdResult.Value.GetRawText());

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/api/workflow-settings", request.Path);
                Assert.Null(request.Body);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/workflow-settings", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                var root = body.RootElement;
                Assert.Equal("routing", root.GetProperty("namespace").GetString());
                Assert.Equal("policy", root.GetProperty("name").GetString());
                Assert.Equal("Typed routing policy.", root.GetProperty("description").GetString());
                var value = root.GetProperty("value");
                Assert.Equal(JsonValueKind.Object, value.ValueKind);
                Assert.Equal("ops", value.GetProperty("roles")[0].GetString());
                Assert.Equal(7.5, value.GetProperty("threshold").GetDouble());
                Assert.False(value.GetProperty("nested").GetProperty("enabled").GetBoolean());
            });
    }

    [Fact]
    public async Task WorkflowSettings_UpdateAndDelete_SendTypedValueConcurrencyAndEncodedTimestamp()
    {
        var expectedUpdatedAt = DateTimeOffset.Parse("2026-08-03T14:05:06.1234567+03:00");
        var value = Json("""["first",42,true,null,{"code":"last"}]""");
        var updated = new WorkflowSettingDto(
            44,
            "examples",
            "categories",
            value,
            "Ordered example categories.",
            expectedUpdatedAt.AddDays(-3),
            expectedUpdatedAt.AddMinutes(2));
        using var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, updated),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var result = await client.UpdateWorkflowSettingAsync(
            44,
            new UpdateWorkflowSettingRequest(
                value,
                "Ordered example categories.",
                expectedUpdatedAt));
        await client.DeleteWorkflowSettingAsync(44, expectedUpdatedAt);

        Assert.Equal(value.GetRawText(), result.Value.GetRawText());
        Assert.Equal("Ordered example categories.", result.Description);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/api/workflow-settings/44", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                var root = body.RootElement;
                Assert.Equal(
                    "Ordered example categories.",
                    root.GetProperty("description").GetString());
                Assert.Equal(
                    expectedUpdatedAt,
                    root.GetProperty("expectedUpdatedAt").GetDateTimeOffset());
                var sentValue = root.GetProperty("value");
                Assert.Equal(JsonValueKind.Array, sentValue.ValueKind);
                Assert.Equal(42, sentValue[1].GetInt32());
                Assert.True(sentValue[2].GetBoolean());
                Assert.Equal(JsonValueKind.Null, sentValue[3].ValueKind);
                Assert.Equal("last", sentValue[4].GetProperty("code").GetString());
                Assert.False(root.TryGetProperty("name", out _));
                Assert.False(root.TryGetProperty("namespace", out _));
            },
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                var timestamp = Uri.EscapeDataString(
                    expectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
                Assert.Equal(
                    $"/api/workflow-settings/44?expectedUpdatedAt={timestamp}",
                    request.Path);
                Assert.Null(request.Body);
            });
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true, "/api/engine-settings")]
    [InlineData(HttpStatusCode.Conflict, false, "/api/workflow-settings")]
    public async Task SettingsFailures_SurfaceStatusCodeAndProblemDetail(
        HttpStatusCode statusCode,
        bool engineRequest,
        string expectedPath)
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                """{"detail":"Settings operation was rejected."}""",
                Encoding.UTF8,
                "application/problem+json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var error = engineRequest
            ? await Assert.ThrowsAsync<WorkflowApiException>(() => client.GetEngineSettingsAsync())
            : await Assert.ThrowsAsync<WorkflowApiException>(() =>
                client.CreateWorkflowSettingAsync(new CreateWorkflowSettingRequest(
                    null,
                    "duplicate",
                    Json("true"),
                    "Duplicate setting.")));

        Assert.Equal(statusCode, error.StatusCode);
        Assert.Equal("Settings operation was rejected.", error.Message);
        Assert.Equal(expectedPath, Assert.Single(handler.Requests).Path);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage Response<T>(HttpStatusCode statusCode, T value) =>
        new(statusCode)
        {
            Content = JsonContent.Create(value)
        };

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

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
            return responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);
}
