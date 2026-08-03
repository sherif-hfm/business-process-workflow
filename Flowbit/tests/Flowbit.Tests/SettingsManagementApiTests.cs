using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SettingsManagementApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("/api/engine-settings")]
    [InlineData("/api/workflow-settings")]
    public async Task SettingsManagementRequiresAuthentication(string path)
    {
        using var response = await fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/engine-settings")]
    [InlineData("/api/workflow-settings")]
    public async Task SettingsManagementRejectsActorsWithoutTheRequiredRole(string path)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            path,
            user: "settings-viewer",
            roles: ["viewer"],
            suppressAdmin: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EngineSettingsCrudPreservesIdentifiersAndEnforcesOptimisticConcurrency()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var settingNamespace = $"api-engine-{suffix}";
        const string key = "ExecutionMode";
        long? createdId = null;

        try
        {
            EngineSettingDto created;
            using (var response = await SendAsync(
                       HttpMethod.Post,
                       "/api/engine-settings",
                       new CreateEngineSettingRequest(
                           $"  {settingNamespace}  ",
                           $"  {key}  ",
                           "  initial  ",
                           "  Initial engine setting description.  ")))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                Assert.Equal(
                    $"/api/engine-settings/{await ReadIdAsync(response)}",
                    response.Headers.Location?.OriginalString);
                created = await ReadAsync<EngineSettingDto>(response);
            }

            createdId = created.Id;
            Assert.Equal(settingNamespace, created.Namespace);
            Assert.Equal(key, created.Key);
            Assert.Equal("  initial  ", created.Value);
            Assert.Equal("Initial engine setting description.", created.Description);
            Assert.Equal(created.CreatedAt, created.UpdatedAt);

            using (var response = await SendAsync(HttpMethod.Get, "/api/engine-settings"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var listed = await ReadAsync<List<EngineSettingDto>>(response);
                var item = Assert.Single(listed, setting => setting.Id == created.Id);
                Assert.Equal(created, item);
            }

            EngineSettingDto updated;
            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/engine-settings/{created.Id}",
                       new Dictionary<string, object?>
                       {
                           ["namespace"] = "attempted-rename",
                           ["key"] = "AttemptedRename",
                           ["value"] = "  updated  ",
                           ["description"] = "  Updated engine setting description.  ",
                           ["expectedUpdatedAt"] = created.UpdatedAt
                       }))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                updated = await ReadAsync<EngineSettingDto>(response);
            }

            Assert.Equal(created.Id, updated.Id);
            Assert.Equal(created.Namespace, updated.Namespace);
            Assert.Equal(created.Key, updated.Key);
            Assert.Equal("  updated  ", updated.Value);
            Assert.Equal("Updated engine setting description.", updated.Description);
            Assert.True(updated.UpdatedAt > created.UpdatedAt);

            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/engine-settings/{created.Id}",
                       new UpdateEngineSettingRequest(
                           "stale-update",
                           "This update must not win.",
                           created.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }

            using (var response = await SendAsync(
                       HttpMethod.Delete,
                       DeletePath("/api/engine-settings", created.Id, created.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }

            var missingId = long.MaxValue;
            var missingTimestamp = DateTimeOffset.UtcNow;
            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/engine-settings/{missingId}",
                       new UpdateEngineSettingRequest(
                           "missing",
                           null,
                           missingTimestamp)))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            using (var response = await SendAsync(
                       HttpMethod.Delete,
                       DeletePath("/api/engine-settings", missingId, missingTimestamp)))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            using (var response = await SendAsync(
                       HttpMethod.Delete,
                       DeletePath("/api/engine-settings", updated.Id, updated.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            createdId = null;
        }
        finally
        {
            await DeleteEngineTestRowsAsync(settingNamespace, createdId);
        }
    }

    [Fact]
    public async Task EngineSettingCanonicalIdentifierConflictsWithLegacyDottedKey()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var settingNamespace = $"api-legacy-{suffix}";
        const string key = "Limit";
        long? legacyId = null;

        try
        {
            using (var response = await SendAsync(
                       HttpMethod.Post,
                       "/api/engine-settings",
                       new CreateEngineSettingRequest(
                           null,
                           $"{settingNamespace}.{key}",
                           "legacy-value",
                           "Legacy dotted-key representation.")))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                legacyId = (await ReadAsync<EngineSettingDto>(response)).Id;
            }

            using var duplicate = await SendAsync(
                HttpMethod.Post,
                "/api/engine-settings",
                new CreateEngineSettingRequest(
                    settingNamespace,
                    key,
                    "canonical-value",
                    "Canonical representation."));

            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }
        finally
        {
            await DeleteEngineTestRowsAsync(settingNamespace, legacyId);
        }
    }

    [Fact]
    public async Task WorkflowSettingsCrudPreservesEveryJsonRootTypeAndRejectsCaseInsensitiveDuplicates()
    {
        var settingNamespace = $"api-workflow-{Guid.NewGuid():N}";
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["value-string"] = Json("\"typed value\""),
            ["value-number"] = Json("12.5"),
            ["value-boolean"] = Json("true"),
            ["value-null"] = Json("null"),
            ["value-array"] = Json("""[1,"two",false,null,{"nested":true}]"""),
            ["value-object"] = Json(
                """{"name":"policy","limit":3,"enabled":true,"fallback":null,"items":[1,"two"]}""")
        };
        var created = new Dictionary<string, WorkflowSettingDto>(StringComparer.Ordinal);

        try
        {
            foreach (var input in inputs)
            {
                using var response = await SendAsync(
                    HttpMethod.Post,
                    "/api/workflow-settings",
                    new CreateWorkflowSettingRequest(
                        settingNamespace,
                        input.Key,
                        input.Value,
                        $"  Description for {input.Key}.  "));
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);

                var item = await ReadAsync<WorkflowSettingDto>(response);
                created.Add(input.Key, item);
                Assert.Equal(settingNamespace, item.Namespace);
                Assert.Equal(input.Key, item.Name);
                Assert.Equal($"Description for {input.Key}.", item.Description);
                AssertJsonEqual(input.Value, item.Value);
            }

            using (var response = await SendAsync(HttpMethod.Get, "/api/workflow-settings"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var listed = (await ReadAsync<List<WorkflowSettingDto>>(response))
                    .Where(setting => setting.Namespace == settingNamespace)
                    .ToDictionary(setting => setting.Name, StringComparer.Ordinal);
                Assert.Equal(inputs.Count, listed.Count);
                foreach (var input in inputs)
                {
                    AssertJsonEqual(input.Value, listed[input.Key].Value);
                    Assert.Equal(created[input.Key].Description, listed[input.Key].Description);
                }
            }

            using (var duplicate = await SendAsync(
                       HttpMethod.Post,
                       "/api/workflow-settings",
                       new CreateWorkflowSettingRequest(
                           settingNamespace.ToUpperInvariant(),
                           "VALUE-STRING",
                           Json("false"),
                           "Case-insensitive duplicate.")))
            {
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            }

            var original = created["value-object"];
            var replacement = Json(
                """["updated",42,true,null,{"deep":{"values":[1,2,3]}}]""");
            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/workflow-settings/{original.Id}",
                       new UpdateWorkflowSettingRequest(
                           replacement,
                           "  Updated typed JSON value.  ",
                           original.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var updated = await ReadAsync<WorkflowSettingDto>(response);
                Assert.Equal(original.Namespace, updated.Namespace);
                Assert.Equal(original.Name, updated.Name);
                Assert.Equal("Updated typed JSON value.", updated.Description);
                Assert.True(updated.UpdatedAt > original.UpdatedAt);
                AssertJsonEqual(replacement, updated.Value);
                created[original.Name] = updated;
            }

            foreach (var item in created.Values)
            {
                using var response = await SendAsync(
                    HttpMethod.Delete,
                    DeletePath("/api/workflow-settings", item.Id, item.UpdatedAt));
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            created.Clear();
        }
        finally
        {
            await DeleteWorkflowTestRowsAsync(settingNamespace, created.Values.Select(item => item.Id));
        }
    }

    [Fact]
    public async Task RequiredRoleChangeAppliesToTheNextRequest()
    {
        var snapshot = await ReadRequiredRoleSnapshotAsync();
        var requiredRole = $"settings-manager-{Guid.NewGuid():N}";

        try
        {
            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/engine-settings/{snapshot.Id}",
                       new UpdateEngineSettingRequest(
                           requiredRole,
                           snapshot.Description,
                           snapshot.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using (var response = await SendAsync(
                       HttpMethod.Get,
                       "/api/workflow-settings",
                       user: "dynamic-settings-manager",
                       roles: [requiredRole.ToUpperInvariant()],
                       suppressAdmin: true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using (var response = await SendAsync(
                       HttpMethod.Get,
                       "/api/engine-settings",
                       user: "former-settings-admin",
                       roles: ["admin"],
                       suppressAdmin: true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        finally
        {
            await RestoreRequiredRoleAsync(snapshot);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "settings-admin",
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

    private static async Task<long> ReadIdAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private static string DeletePath(
        string basePath,
        long id,
        DateTimeOffset expectedUpdatedAt) =>
        $"{basePath}/{id}?expectedUpdatedAt={Uri.EscapeDataString(expectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture))}";

    private async Task DeleteEngineTestRowsAsync(string marker, long? id)
    {
        await using var db = fixture.CreateDbContext();
        await db.EngineSettings
            .Where(setting =>
                (id.HasValue && setting.Id == id.Value)
                || setting.Namespace == marker
                || ((setting.Namespace == null || setting.Namespace == string.Empty)
                    && EF.Functions.Like(setting.Key, marker + ".%")))
            .ExecuteDeleteAsync();
    }

    private async Task DeleteWorkflowTestRowsAsync(
        string settingNamespace,
        IEnumerable<long> ids)
    {
        var idArray = ids.Distinct().ToArray();
        await using var db = fixture.CreateDbContext();
        await db.WorkflowSettings
            .Where(setting =>
                setting.Namespace == settingNamespace
                || idArray.Contains(setting.Id))
            .ExecuteDeleteAsync();
    }

    private async Task<RequiredRoleSnapshot> ReadRequiredRoleSnapshotAsync()
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings
            .AsNoTracking()
            .SingleAsync(candidate =>
                (candidate.Namespace == "Settings" && candidate.Key == "RequiredRole")
                || ((candidate.Namespace == null || candidate.Namespace == string.Empty)
                    && candidate.Key == "Settings.RequiredRole"));
        return new RequiredRoleSnapshot(
            setting.Id,
            setting.Namespace,
            setting.Key,
            setting.Value,
            setting.Description,
            setting.CreatedAt,
            setting.UpdatedAt);
    }

    private async Task RestoreRequiredRoleAsync(RequiredRoleSnapshot snapshot)
    {
        await using var db = fixture.CreateDbContext();
        var restored = await db.EngineSettings
            .Where(setting => setting.Id == snapshot.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(setting => setting.Namespace, snapshot.Namespace)
                .SetProperty(setting => setting.Key, snapshot.Key)
                .SetProperty(setting => setting.Value, snapshot.Value)
                .SetProperty(setting => setting.Description, snapshot.Description)
                .SetProperty(setting => setting.CreatedAt, snapshot.CreatedAt)
                .SetProperty(setting => setting.UpdatedAt, snapshot.UpdatedAt));
        Assert.Equal(1, restored);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertJsonEqual(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var expectedProperties = expected.EnumerateObject().ToArray();
                var actualProperties = actual.EnumerateObject().ToArray();
                Assert.Equal(expectedProperties.Length, actualProperties.Length);
                foreach (var property in expectedProperties)
                {
                    Assert.True(actual.TryGetProperty(property.Name, out var actualValue));
                    AssertJsonEqual(property.Value, actualValue);
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                Assert.Equal(expectedItems.Length, actualItems.Length);
                for (var index = 0; index < expectedItems.Length; index++)
                {
                    AssertJsonEqual(expectedItems[index], actualItems[index]);
                }
                break;
            }
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetDecimal(), actual.GetDecimal());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), actual.GetBoolean());
                break;
            case JsonValueKind.Null:
                break;
            default:
                throw new InvalidOperationException($"Unexpected JSON kind {expected.ValueKind}.");
        }
    }

    private sealed record RequiredRoleSnapshot(
        long Id,
        string? Namespace,
        string Key,
        string Value,
        string? Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
