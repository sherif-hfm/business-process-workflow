extern alias FlowbitUi;

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EngineSettingsPage = FlowbitUi::Flowbit.Ui.Components.Pages.EngineSettings;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowSettingsPage = FlowbitUi::Flowbit.Ui.Components.Pages.WorkflowSettings;
using Xunit;

namespace Flowbit.Tests;

public sealed class SettingsManagementPageStateTests
{
    public static TheoryData<string, JsonValueKind> WorkflowJsonRoots => new()
    {
        { "\"text\"", JsonValueKind.String },
        { "42.5", JsonValueKind.Number },
        { "true", JsonValueKind.True },
        { "false", JsonValueKind.False },
        { "null", JsonValueKind.Null },
        { "[1,\"two\",false,null]", JsonValueKind.Array },
        { "{\"enabled\":true,\"nested\":{\"count\":2}}", JsonValueKind.Object }
    };

    public static TheoryData<string?, string?, string> EffectiveIdentifiers => new()
    {
        { null, "  Settings.RequiredRole  ", "Settings.RequiredRole" },
        { "   ", "  examples.messageClientId  ", "examples.messageClientId" },
        { " Workflow ", " AutomaticHopLimit ", "Workflow.AutomaticHopLimit" },
        { "examples", " categories ", "examples.categories" }
    };

    public static TheoryData<Type> SettingsPages => new()
    {
        typeof(EngineSettingsPage),
        typeof(WorkflowSettingsPage)
    };

    [Theory]
    [MemberData(nameof(WorkflowJsonRoots))]
    public void WorkflowJsonParser_AcceptsEveryRootKindAndReturnsDetachedClone(
        string raw,
        JsonValueKind expectedKind)
    {
        var arguments = new object?[] { raw, default(JsonElement), null };

        var parsed = Assert.IsType<bool>(InvokeStatic(
            typeof(WorkflowSettingsPage),
            "TryParseValue",
            arguments));

        Assert.True(parsed);
        var value = Assert.IsType<JsonElement>(arguments[1]);
        Assert.Equal(expectedKind, value.ValueKind);
        Assert.Null(arguments[2]);

        // Reading the raw text after TryParseValue returns proves that the value was
        // cloned before the method's JsonDocument was disposed.
        Assert.Equal(raw, value.GetRawText());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("undefined")]
    [InlineData("{")]
    [InlineData("1 2")]
    public void WorkflowJsonParser_RejectsInvalidOrUndefinedInput(string? raw)
    {
        var arguments = new object?[] { raw, default(JsonElement), null };

        var parsed = Assert.IsType<bool>(InvokeStatic(
            typeof(WorkflowSettingsPage),
            "TryParseValue",
            arguments));

        Assert.False(parsed);
        Assert.Equal(JsonValueKind.Undefined, Assert.IsType<JsonElement>(arguments[1]).ValueKind);
        Assert.StartsWith("Enter a valid JSON value.", Assert.IsType<string>(arguments[2]));
    }

    [Fact]
    public void WorkflowJsonFormatting_PrettyPrintsAndProducesBoundedCompactPreviews()
    {
        var value = Json("""{"enabled":true,"items":[1,2],"nested":{"name":"flow"}}""");

        var pretty = Assert.IsType<string>(InvokeStatic(
            typeof(WorkflowSettingsPage),
            "PrettyPrint",
            value));
        var preview = Assert.IsType<string>(InvokeStatic(
            typeof(WorkflowSettingsPage),
            "PreviewValue",
            value));

        Assert.Contains("\n", pretty);
        Assert.Contains("  \"items\"", pretty);
        Assert.Equal(value.GetRawText(), preview);
        Assert.Equal(string.Empty, InvokeStatic(
            typeof(WorkflowSettingsPage),
            "PrettyPrint",
            default(JsonElement)));
        Assert.Equal("(undefined)", InvokeStatic(
            typeof(WorkflowSettingsPage),
            "PreviewValue",
            default(JsonElement)));

        var longValue = Json($"\"{new string('x', 120)}\"");
        var longPreview = Assert.IsType<string>(InvokeStatic(
            typeof(WorkflowSettingsPage),
            "PreviewValue",
            longValue));
        Assert.Equal(101, longPreview.Length);
        Assert.EndsWith("…", longPreview);
        Assert.StartsWith(JsonSerializer.Serialize(longValue)[..100], longPreview);
    }

    [Fact]
    public void EngineValuePreview_RepresentsEmptyAndEscapesMultilineValues()
    {
        Assert.Equal("(empty string)", InvokeStatic(
            typeof(EngineSettingsPage),
            "PreviewValue",
            string.Empty));
        Assert.Equal("first\\r\\nsecond\\tvalue", InvokeStatic(
            typeof(EngineSettingsPage),
            "PreviewValue",
            "first\r\nsecond\tvalue"));

        var preview = Assert.IsType<string>(InvokeStatic(
            typeof(EngineSettingsPage),
            "PreviewValue",
            new string('x', 120)));
        Assert.Equal(101, preview.Length);
        Assert.EndsWith("…", preview);
    }

    [Theory]
    [MemberData(nameof(EffectiveIdentifiers))]
    public void EffectiveKey_TrimsNamespaceAndKey(
        string? settingNamespace,
        string? key,
        string expected)
    {
        Assert.Equal(expected, InvokeStatic(
            typeof(EngineSettingsPage),
            "EffectiveKey",
            settingNamespace,
            key));
    }

    [Theory]
    [MemberData(nameof(EffectiveIdentifiers))]
    public void EffectiveName_TrimsNamespaceAndName(
        string? settingNamespace,
        string? name,
        string expected)
    {
        Assert.Equal(expected, InvokeStatic(
            typeof(WorkflowSettingsPage),
            "EffectiveName",
            settingNamespace,
            name));
    }

    [Fact]
    public async Task EngineEditConflict_PreservesEnteredFormState()
    {
        var page = new EngineSettingsPage();
        SetProperty(page, "Api", Client(new ConflictHandler()));
        var expectedUpdatedAt = DateTimeOffset.Parse("2026-08-03T10:15:30Z");
        SetField(page, "editingId", 7L);
        SetField(page, "expectedUpdatedAt", expectedUpdatedAt);
        SetField(page, "formOpen", true);
        SetField(page, "formNamespace", "Settings");
        SetField(page, "formKey", "RequiredRole");
        SetField(page, "formValue", "admin,operations");
        SetField(page, "formDescription", "Edited role policy");

        await InvokeAsync(page, "SaveAsync");

        Assert.True(GetField<bool>(page, "formOpen"));
        Assert.Equal(7L, GetField<long?>(page, "editingId"));
        Assert.Equal(expectedUpdatedAt, GetField<DateTimeOffset?>(page, "expectedUpdatedAt"));
        Assert.Equal("Settings", GetField<string?>(page, "formNamespace"));
        Assert.Equal("RequiredRole", GetField<string?>(page, "formKey"));
        Assert.Equal("admin,operations", GetField<string>(page, "formValue"));
        Assert.Equal("Edited role policy", GetField<string?>(page, "formDescription"));
        Assert.True(GetField<bool>(page, "editConflict"));
        Assert.Contains("edits are preserved", GetField<string>(page, "formError"));
        Assert.False(GetField<bool>(page, "submitting"));
    }

    [Fact]
    public async Task WorkflowEditConflict_PreservesRawJsonFormState()
    {
        var page = new WorkflowSettingsPage();
        SetProperty(page, "Api", Client(new ConflictHandler()));
        var expectedUpdatedAt = DateTimeOffset.Parse("2026-08-03T10:16:30Z");
        const string rawJson = """{"roles":["admin"],"enabled":true}""";
        SetField(page, "editingId", 8L);
        SetField(page, "expectedUpdatedAt", expectedUpdatedAt);
        SetField(page, "formOpen", true);
        SetField(page, "formNamespace", "examples");
        SetField(page, "formName", "accessPolicy");
        SetField(page, "formValue", rawJson);
        SetField(page, "formDescription", "Edited JSON policy");

        await InvokeAsync(page, "SaveAsync");

        Assert.True(GetField<bool>(page, "formOpen"));
        Assert.Equal(8L, GetField<long?>(page, "editingId"));
        Assert.Equal(expectedUpdatedAt, GetField<DateTimeOffset?>(page, "expectedUpdatedAt"));
        Assert.Equal("examples", GetField<string?>(page, "formNamespace"));
        Assert.Equal("accessPolicy", GetField<string?>(page, "formName"));
        Assert.Equal(rawJson, GetField<string>(page, "formValue"));
        Assert.Equal("Edited JSON policy", GetField<string?>(page, "formDescription"));
        Assert.True(GetField<bool>(page, "editConflict"));
        Assert.Contains("edits are preserved", GetField<string>(page, "formError"));
        Assert.False(GetField<bool>(page, "submitting"));
    }

    [Theory]
    [MemberData(nameof(SettingsPages))]
    public async Task IdentityChange_InvalidatesInFlightSaveContinuation(Type pageType)
    {
        var handler = new BlockingSuccessHandler();
        var page = Activator.CreateInstance(pageType)!;
        SetProperty(page, "Api", Client(handler));
        SetField(page, "editingId", 7L);
        SetField(page, "expectedUpdatedAt", DateTimeOffset.Parse("2026-08-03T10:15:30Z"));
        SetField(page, "formOpen", true);
        SetField(page, "formNamespace", "old");
        SetField(page, pageType == typeof(EngineSettingsPage) ? "formKey" : "formName", "oldSetting");
        SetField(page, "formValue", pageType == typeof(EngineSettingsPage) ? "old value" : "true");
        SetField(page, "formDescription", "old description");

        var saveTask = Assert.IsAssignableFrom<Task>(Invoke(page, "SaveAsync"));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Simulate a token change followed by a new-identity operation while the
        // original request is still in flight.
        SetField(page, "identityVersion", 1L);
        SetField(page, "formOpen", true);
        SetField(page, "editingId", 99L);
        SetField(page, "formNamespace", "new");
        SetField(page, pageType == typeof(EngineSettingsPage) ? "formKey" : "formName", "newSetting");
        SetField(page, "formValue", pageType == typeof(EngineSettingsPage) ? "new value" : "false");
        SetField(page, "formDescription", "new description");
        SetField(page, "formError", "new identity form state");
        SetField(page, "success", "new identity success");
        SetField(page, "submitting", true);

        handler.Release.TrySetResult();
        await saveTask;

        Assert.True(GetField<bool>(page, "formOpen"));
        Assert.Equal(99L, GetField<long?>(page, "editingId"));
        Assert.Equal("new", GetField<string?>(page, "formNamespace"));
        Assert.Equal("newSetting", GetField<string?>(
            page,
            pageType == typeof(EngineSettingsPage) ? "formKey" : "formName"));
        Assert.Equal(pageType == typeof(EngineSettingsPage) ? "new value" : "false",
            GetField<string>(page, "formValue"));
        Assert.Equal("new description", GetField<string?>(page, "formDescription"));
        Assert.Equal("new identity form state", GetField<string?>(page, "formError"));
        Assert.Equal("new identity success", GetField<string?>(page, "success"));
        Assert.True(GetField<bool>(page, "submitting"));
    }

    [Theory]
    [MemberData(nameof(SettingsPages))]
    public async Task LoadWithoutToken_IsGatedBeforeApiAccess(Type pageType)
    {
        var page = Activator.CreateInstance(pageType)!;
        SetProperty(page, "Token", new TokenState());
        SetField(page, "loading", true);
        SetField(page, "loadFailed", true);
        SetField(page, "error", "stale error");

        await InvokeAsync(page, "LoadAsync");

        Assert.Null(Field(page, "settings").GetValue(page));
        Assert.False(GetField<bool>(page, "loading"));
        Assert.False(GetField<bool>(page, "loadFailed"));
        Assert.Null(GetField<string?>(page, "error"));
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static WorkflowApiClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://flowbit.test") });

    private static async Task InvokeAsync(object target, string name)
    {
        var task = Assert.IsAssignableFrom<Task>(Invoke(target, name));
        await task;
    }

    private static object? Invoke(object target, string name, params object?[] arguments) =>
        Method(target.GetType(), name, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(target, arguments);

    private static object? InvokeStatic(Type type, string name, params object?[] arguments) =>
        Method(type, name, BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, arguments);

    private static MethodInfo Method(Type type, string name, BindingFlags flags) =>
        type.GetMethod(name, flags)
        ?? throw new InvalidOperationException($"Method '{name}' was not found on {type.Name}.");

    private static T GetField<T>(object target, string name) =>
        (T)(Field(target, name).GetValue(target)
            ?? (default(T) is null ? default! : throw new InvalidOperationException($"Field '{name}' is null.")));

    private static void SetField(object target, string name, object? value) =>
        Field(target, name).SetValue(target, value);

    private static FieldInfo Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Field '{name}' was not found on {target.GetType().Name}.");

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{name}' was not found on {target.GetType().Name}.");
        property.SetValue(target, value);
    }

    private sealed class ConflictHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    "{\"detail\":\"The setting changed.\"}",
                    Encoding.UTF8,
                    "application/problem+json")
            });
    }

    private sealed class BlockingSuccessHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);

            var json = request.RequestUri?.AbsolutePath.Contains(
                "workflow-settings",
                StringComparison.Ordinal) == true
                ? """{"id":7,"namespace":"old","name":"oldSetting","value":true,"description":"old description","createdAt":"2026-08-03T10:00:00Z","updatedAt":"2026-08-03T10:16:00Z"}"""
                : """{"id":7,"namespace":"old","key":"oldSetting","value":"old value","description":"old description","createdAt":"2026-08-03T10:00:00Z","updatedAt":"2026-08-03T10:16:00Z"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
