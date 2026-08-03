using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SettingsManagementPersistenceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task EngineSettings_NormalizeIdentifiersListDeterministicallyAndSupportDescriptionCrud()
    {
        var marker = Marker();
        var opaqueValue = "  line one\r\nline two\t ";

        try
        {
            long rootId;
            DateTimeOffset rootCreatedAt;
            DateTimeOffset originalUpdatedAt;

            await using (var createScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = createScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsService>();

                var root = await service.CreateAsync(
                    "   ",
                    $"  {marker}-root  ",
                    opaqueValue,
                    "  Initial description.  ",
                    CancellationToken.None);
                await service.CreateAsync(
                    $"  {marker}  ",
                    "  omega  ",
                    "omega-value",
                    null,
                    CancellationToken.None);
                await service.CreateAsync(
                    marker,
                    "alpha",
                    "alpha-value",
                    "   ",
                    CancellationToken.None);

                Assert.Null(root.Namespace);
                Assert.Equal($"{marker}-root", root.Key);
                Assert.Equal(opaqueValue, root.Value);
                Assert.Equal("Initial description.", root.Description);

                rootId = root.Id;
                rootCreatedAt = root.CreatedAt;
                originalUpdatedAt = root.UpdatedAt;
            }

            await using (var listScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = listScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsService>();
                var listed = (await service.ListAsync(CancellationToken.None))
                    .Where(setting => BelongsToMarker(
                        setting.Namespace,
                        setting.Key,
                        marker))
                    .ToArray();

                Assert.Equal(
                    new[]
                    {
                        $"/{marker}-root",
                        $"{marker}/alpha",
                        $"{marker}/omega"
                    },
                    listed.Select(setting => $"{setting.Namespace}/{setting.Key}"));
                Assert.Equal(opaqueValue, listed[0].Value);
                Assert.Equal("Initial description.", listed[0].Description);
                Assert.Null(listed[1].Description);
            }

            DateTimeOffset currentUpdatedAt;
            await using (var updateScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = updateScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsService>();
                var updated = await service.UpdateAsync(
                    rootId,
                    string.Empty,
                    "  Revised description.  ",
                    originalUpdatedAt,
                    CancellationToken.None);

                Assert.NotNull(updated);
                Assert.Equal(rootId, updated.Id);
                Assert.Equal(rootCreatedAt, updated.CreatedAt);
                Assert.Equal(string.Empty, updated.Value);
                Assert.Equal("Revised description.", updated.Description);
                Assert.True(updated.UpdatedAt > originalUpdatedAt);
                currentUpdatedAt = updated.UpdatedAt;

                await Assert.ThrowsAsync<WorkflowConflictException>(() =>
                    service.UpdateAsync(
                        rootId,
                        "stale-value",
                        "stale description",
                        originalUpdatedAt,
                        CancellationToken.None));
                await Assert.ThrowsAsync<WorkflowConflictException>(() =>
                    service.DeleteByIdAsync(
                        rootId,
                        originalUpdatedAt,
                        CancellationToken.None));
            }

            await using (var deleteScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = deleteScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsService>();
                Assert.True(await service.DeleteByIdAsync(
                    rootId,
                    currentUpdatedAt,
                    CancellationToken.None));
            }

            await using (var verifyScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = verifyScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsService>();
                Assert.DoesNotContain(
                    await service.ListAsync(CancellationToken.None),
                    setting => setting.Id == rootId);
            }
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    [Fact]
    public async Task EngineSettings_RejectCanonicalAndLegacyFormsOfTheSameLogicalKey()
    {
        var marker = Marker();
        var firstNamespace = $"{marker}-canonical-first";
        var secondNamespace = $"{marker}-legacy-first";

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<IEngineSettingsService>();

            await service.CreateAsync(
                firstNamespace,
                "duplicate",
                "canonical",
                null,
                CancellationToken.None);
            await Assert.ThrowsAsync<WorkflowConflictException>(() =>
                service.CreateAsync(
                    null,
                    $"{firstNamespace}.duplicate",
                    "legacy",
                    null,
                    CancellationToken.None));

            await service.CreateAsync(
                null,
                $"{secondNamespace}.duplicate",
                "legacy",
                null,
                CancellationToken.None);
            await Assert.ThrowsAsync<WorkflowConflictException>(() =>
                service.CreateAsync(
                    secondNamespace,
                    "duplicate",
                    "canonical",
                    null,
                    CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    [Fact]
    public async Task EngineSettings_NamespacedDottedKeyIsReachableByItsEffectiveKey()
    {
        var marker = Marker();
        const string dottedKey = "feature.enabled";
        var effectiveKey = $"{marker}.{dottedKey}";

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<IEngineSettingsService>();

            var created = await service.CreateAsync(
                marker,
                dottedKey,
                "created",
                "Dotted namespaced key.",
                CancellationToken.None);

            var loaded = await service.GetByKeyAsync(effectiveKey, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(created.Id, loaded.Id);
            Assert.Equal("created", loaded.Value);

            var updated = await service.SetAsync(
                effectiveKey,
                "updated",
                CancellationToken.None);
            Assert.Equal(created.Id, updated.Id);
            Assert.Equal("updated", updated.Value);
            Assert.Equal("Dotted namespaced key.", updated.Description);

            Assert.True(await service.DeleteAsync(effectiveKey, CancellationToken.None));
            Assert.Null(await service.GetByKeyAsync(effectiveKey, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    [Fact]
    public async Task EngineSettings_CanonicalRepresentationWinsOverLegacyDuplicate()
    {
        var marker = Marker();
        const string key = "RequiredRole";
        var effectiveKey = $"{marker}.{key}";

        try
        {
            long canonicalId;
            long legacyId;
            await using (var setup = fixture.CreateDbContext())
            {
                var now = DateTimeOffset.UtcNow;
                var canonical = new EngineSettingEntity
                {
                    Namespace = marker,
                    Key = key,
                    Value = "canonical",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                var legacy = new EngineSettingEntity
                {
                    Namespace = null,
                    Key = effectiveKey,
                    Value = "legacy",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                setup.EngineSettings.AddRange(canonical, legacy);
                await setup.SaveChangesAsync();
                canonicalId = canonical.Id;
                legacyId = legacy.Id;
            }

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<IEngineSettingsService>();

            var loaded = await service.GetByKeyAsync(effectiveKey, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(canonicalId, loaded.Id);
            Assert.Equal("canonical", loaded.Value);

            var updated = await service.SetAsync(
                effectiveKey,
                "canonical-updated",
                CancellationToken.None);
            Assert.Equal(canonicalId, updated.Id);

            await using var verify = fixture.CreateDbContext();
            Assert.Equal(
                "canonical-updated",
                (await verify.EngineSettings.AsNoTracking()
                    .SingleAsync(setting => setting.Id == canonicalId)).Value);
            Assert.Equal(
                "legacy",
                (await verify.EngineSettings.AsNoTracking()
                    .SingleAsync(setting => setting.Id == legacyId)).Value);
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    [Fact]
    public async Task EngineSettings_WhitespaceNamespaceActsAsLegacyBlankNamespace()
    {
        var marker = Marker();
        var effectiveKey = $"{marker}.RequiredRole";

        try
        {
            long legacyId;
            await using (var setup = fixture.CreateDbContext())
            {
                var now = DateTimeOffset.UtcNow;
                var legacy = new EngineSettingEntity
                {
                    Namespace = "   ",
                    Key = effectiveKey,
                    Value = "operator",
                    Description = "Legacy whitespace namespace.",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                setup.EngineSettings.Add(legacy);
                await setup.SaveChangesAsync();
                legacyId = legacy.Id;
            }

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<IEngineSettingsService>();

            var loaded = await service.GetByKeyAsync(effectiveKey, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(legacyId, loaded.Id);
            Assert.Equal("operator", loaded.Value);
            Assert.Contains(
                await service.SearchAsync(effectiveKey, CancellationToken.None),
                setting => setting.Id == legacyId);

            var updated = await service.SetAsync(
                effectiveKey,
                "updated",
                CancellationToken.None);
            Assert.Equal(legacyId, updated.Id);
            Assert.Equal("updated", updated.Value);
            Assert.Equal("Legacy whitespace namespace.", updated.Description);

            Assert.True(await service.DeleteAsync(effectiveKey, CancellationToken.None));
            Assert.Null(await service.GetByKeyAsync(effectiveKey, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    [Fact]
    public async Task EngineSettings_UpdateReturnsItsOwnCommittedValueDuringAConcurrentUpdate()
    {
        var marker = Marker();

        try
        {
            long id;
            DateTimeOffset expectedUpdatedAt;
            await using (var createScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = createScope.ServiceProvider
                    .GetRequiredService<IEngineSettingsService>();
                var created = await service.CreateAsync(
                    marker,
                    "concurrent",
                    "before",
                    null,
                    CancellationToken.None);
                id = created.Id;
                expectedUpdatedAt = created.UpdatedAt;
            }

            var blocker = new PauseAfterUpdateInterceptor("engine_settings");
            var secondStarted = new CommandStartedInterceptor();
            await using var firstDb = CreateDbContext(blocker);
            await using var secondDb = CreateDbContext(secondStarted);
            var firstRepository = new EngineSettingsRepository(firstDb);
            var secondRepository = new EngineSettingsRepository(secondDb);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var firstTask = firstRepository.UpdateAsync(
                id,
                "first",
                "first writer",
                expectedUpdatedAt,
                timeout.Token);
            await blocker.WaitUntilPausedAsync(timeout.Token);

            var secondTask = secondRepository.UpdateAsync(
                id,
                "second",
                "second writer",
                expectedUpdatedAt,
                timeout.Token);
            await secondStarted.WaitUntilStartedAsync(timeout.Token);
            blocker.Release();

            var first = await firstTask;
            Assert.NotNull(first);
            Assert.Equal("first", first.Value);
            Assert.Equal("first writer", first.Description);
            await Assert.ThrowsAsync<WorkflowConflictException>(() => secondTask);
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    [Fact]
    public async Task WorkflowSettings_RoundTripEveryJsonRootAndSupportDescriptionCrud()
    {
        var marker = Marker();
        var cases = new[]
        {
            new JsonCase("01-object", """{"text":"value","nested":{"enabled":true}}"""),
            new JsonCase("02-array", """["value",2,false,null]"""),
            new JsonCase("03-string", "\"text value\""),
            new JsonCase("04-number", "123.5"),
            new JsonCase("05-boolean", "true"),
            new JsonCase("06-null", "null")
        };

        try
        {
            var created = new Dictionary<string, CreatedWorkflowSetting>(StringComparer.Ordinal);
            await using (var createScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = createScope.ServiceProvider
                    .GetRequiredService<IWorkflowSettingsService>();
                foreach (var item in cases)
                {
                    var value = Json(item.Json);
                    var setting = await service.CreateAsync(
                        $"  {marker}  ",
                        $"  {item.Name}  ",
                        value,
                        item.Name == "01-object"
                            ? "  Initial workflow description.  "
                            : "   ",
                        CancellationToken.None);
                    created.Add(
                        item.Name,
                        new CreatedWorkflowSetting(
                            setting.Id,
                            setting.CreatedAt,
                            setting.UpdatedAt));
                }
            }

            await using (var listScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = listScope.ServiceProvider
                    .GetRequiredService<IWorkflowSettingsService>();
                var listed = (await service.ListAsync(CancellationToken.None))
                    .Where(setting => setting.Namespace == marker)
                    .ToArray();

                Assert.Equal(cases.Select(item => item.Name), listed.Select(setting => setting.Name));
                foreach (var item in cases)
                {
                    var setting = Assert.Single(listed, candidate => candidate.Name == item.Name);
                    AssertJsonEqual(item.Json, setting.Value);
                }

                Assert.Equal(
                    "Initial workflow description.",
                    listed[0].Description);
                Assert.All(listed[1..], setting => Assert.Null(setting.Description));
            }

            var objectSetting = created["01-object"];
            DateTimeOffset currentUpdatedAt;
            await using (var updateScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = updateScope.ServiceProvider
                    .GetRequiredService<IWorkflowSettingsService>();
                var updated = await service.UpdateAsync(
                    objectSetting.Id,
                    Json("""{"state":"updated","count":2}"""),
                    "  Revised workflow description.  ",
                    objectSetting.UpdatedAt,
                    CancellationToken.None);

                Assert.NotNull(updated);
                Assert.Equal(objectSetting.CreatedAt, updated.CreatedAt);
                AssertJsonEqual("""{"state":"updated","count":2}""", updated.Value);
                Assert.Equal("Revised workflow description.", updated.Description);
                Assert.True(updated.UpdatedAt > objectSetting.UpdatedAt);
                currentUpdatedAt = updated.UpdatedAt;

                await Assert.ThrowsAsync<WorkflowConflictException>(() =>
                    service.UpdateAsync(
                        objectSetting.Id,
                        Json("false"),
                        "stale description",
                        objectSetting.UpdatedAt,
                        CancellationToken.None));
                await Assert.ThrowsAsync<WorkflowConflictException>(() =>
                    service.DeleteByIdAsync(
                        objectSetting.Id,
                        objectSetting.UpdatedAt,
                        CancellationToken.None));
            }

            await using (var deleteScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = deleteScope.ServiceProvider
                    .GetRequiredService<IWorkflowSettingsService>();
                Assert.True(await service.DeleteByIdAsync(
                    objectSetting.Id,
                    currentUpdatedAt,
                    CancellationToken.None));
            }

            await using (var verifyScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = verifyScope.ServiceProvider
                    .GetRequiredService<IWorkflowSettingsService>();
                Assert.DoesNotContain(
                    await service.ListAsync(CancellationToken.None),
                    setting => setting.Id == objectSetting.Id);
            }
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    [Fact]
    public async Task WorkflowSettings_RejectCaseInsensitiveCanonicalAndLegacyLogicalDuplicates()
    {
        var marker = Marker();
        var settingNamespace = $"{marker}-CaseNamespace";

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<IWorkflowSettingsService>();

            await service.CreateAsync(
                settingNamespace,
                "CaseName",
                Json("true"),
                null,
                CancellationToken.None);

            await Assert.ThrowsAsync<WorkflowConflictException>(() =>
                service.CreateAsync(
                    null,
                    $"{settingNamespace.ToLowerInvariant()}.casename",
                    Json("false"),
                    null,
                    CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    [Fact]
    public async Task WorkflowSettings_NewScopeReloadsCommittedValueAfterEarlierScopeLoadedSettings()
    {
        var marker = Marker();
        const string name = "freshness";
        var dictionaryKey = $"setting.{marker}.{name}";

        try
        {
            long id;
            DateTimeOffset expectedUpdatedAt;
            await using (var createScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = createScope.ServiceProvider
                    .GetRequiredService<IWorkflowSettingsService>();
                var created = await service.CreateAsync(
                    marker,
                    name,
                    Json("""{"state":"before"}"""),
                    "Freshness probe.",
                    CancellationToken.None);
                id = created.Id;
                expectedUpdatedAt = created.UpdatedAt;
            }

            await using var warmScope = fixture.Factory.Services.CreateAsyncScope();
            var warmRepository = warmScope.ServiceProvider
                .GetRequiredService<IWorkflowSettingsRepository>();
            var warmed = await warmRepository.LoadAllAsync(CancellationToken.None);
            AssertJsonEqual("""{"state":"before"}""", warmed[dictionaryKey]);

            await using (var updateScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = updateScope.ServiceProvider
                    .GetRequiredService<IWorkflowSettingsService>();
                var updated = await service.UpdateAsync(
                    id,
                    Json("""{"state":"after","revision":2}"""),
                    "Freshness probe updated.",
                    expectedUpdatedAt,
                    CancellationToken.None);
                Assert.NotNull(updated);
            }

            await using (var reloadScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var repository = reloadScope.ServiceProvider
                    .GetRequiredService<IWorkflowSettingsRepository>();
                var reloaded = await repository.LoadAllAsync(CancellationToken.None);
                AssertJsonEqual(
                    """{"state":"after","revision":2}""",
                    reloaded[dictionaryKey]);
            }

            AssertJsonEqual("""{"state":"before"}""", warmed[dictionaryKey]);
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    [Fact]
    public async Task WorkflowSettings_UpdateReturnsItsOwnCommittedValueDuringAConcurrentUpdate()
    {
        var marker = Marker();

        try
        {
            long id;
            DateTimeOffset expectedUpdatedAt;
            await using (var createScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var service = createScope.ServiceProvider
                    .GetRequiredService<IWorkflowSettingsService>();
                var created = await service.CreateAsync(
                    marker,
                    "concurrent",
                    Json("""{"writer":"before"}"""),
                    null,
                    CancellationToken.None);
                id = created.Id;
                expectedUpdatedAt = created.UpdatedAt;
            }

            var blocker = new PauseAfterUpdateInterceptor("workflow_settings");
            var secondStarted = new CommandStartedInterceptor();
            await using var firstDb = CreateDbContext(blocker);
            await using var secondDb = CreateDbContext(secondStarted);
            var firstRepository = new WorkflowSettingsRepository(firstDb);
            var secondRepository = new WorkflowSettingsRepository(secondDb);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var firstTask = firstRepository.UpdateAsync(
                id,
                Json("""{"writer":"first"}"""),
                "first writer",
                expectedUpdatedAt,
                timeout.Token);
            await blocker.WaitUntilPausedAsync(timeout.Token);

            var secondTask = secondRepository.UpdateAsync(
                id,
                Json("""{"writer":"second"}"""),
                "second writer",
                expectedUpdatedAt,
                timeout.Token);
            await secondStarted.WaitUntilStartedAsync(timeout.Token);
            blocker.Release();

            var first = await firstTask;
            Assert.NotNull(first);
            AssertJsonEqual("""{"writer":"first"}""", first.Value);
            Assert.Equal("first writer", first.Description);
            await Assert.ThrowsAsync<WorkflowConflictException>(() => secondTask);
        }
        finally
        {
            await CleanupAsync(marker);
        }
    }

    private AppDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.DataSource, FlowbitDatabase.ConfigureProvider)
            .AddInterceptors(interceptors)
            .Options;
        return new AppDbContext(options);
    }

    private async Task CleanupAsync(string marker)
    {
        await using var dbContext = fixture.CreateDbContext();
        await dbContext.EngineSettings
            .Where(setting =>
                (setting.Namespace != null && setting.Namespace.StartsWith(marker))
                || setting.Key.StartsWith(marker))
            .ExecuteDeleteAsync();
        await dbContext.WorkflowSettings
            .Where(setting =>
                (setting.Namespace != null && setting.Namespace.StartsWith(marker))
                || setting.Name.StartsWith(marker))
            .ExecuteDeleteAsync();
    }

    private static bool BelongsToMarker(
        string? settingNamespace,
        string key,
        string marker) =>
        string.Equals(settingNamespace, marker, StringComparison.Ordinal)
        || key.StartsWith(marker, StringComparison.Ordinal);

    private static string Marker() =>
        $"settings_it_{Guid.NewGuid():N}";

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertJsonEqual(string expectedJson, JsonElement actual)
    {
        var expected = JsonNode.Parse(expectedJson);
        var observed = JsonNode.Parse(actual.GetRawText());
        Assert.True(
            JsonNode.DeepEquals(expected, observed),
            $"Expected JSON {expectedJson}, but received {actual.GetRawText()}.");
    }

    private sealed record JsonCase(string Name, string Json);

    private sealed record CreatedWorkflowSetting(
        long Id,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed class PauseAfterUpdateInterceptor(string tableName) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource paused = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int intercepted;

        public Task WaitUntilPausedAsync(CancellationToken cancellationToken) =>
            paused.Task.WaitAsync(cancellationToken);

        public void Release() => released.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(tableName, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
            {
                paused.TrySetResult();
                await released.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class CommandStartedInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilStartedAsync(CancellationToken cancellationToken) =>
            started.Task.WaitAsync(cancellationToken);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
