extern alias FlowbitUi;

using System.Net;
using System.Reflection;
using System.Text;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using InboxPage = FlowbitUi::Flowbit.Ui.Components.Pages.Inbox;
using InstancesPage = FlowbitUi::Flowbit.Ui.Components.Pages.Instances;
using NodeActivityPage = FlowbitUi::Flowbit.Ui.Components.Pages.NodeActivity;
using TaskDistributionPage = FlowbitUi::Flowbit.Ui.Components.Pages.TaskDistribution;
using TaskManagementPage = FlowbitUi::Flowbit.Ui.Components.Pages.TaskManagement;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class AdvancedSearchPageStateTests
{
    public static TheoryData<Type> PagedSearchPages => new()
    {
        typeof(InstancesPage),
        typeof(InboxPage),
        typeof(TaskManagementPage),
        typeof(NodeActivityPage),
        typeof(TaskDistributionPage)
    };

    [Theory]
    [MemberData(nameof(PagedSearchPages))]
    public void MarkFiltersDirty_ResetsPagingAndInvalidatesInFlightSearch(Type pageType)
    {
        var page = Activator.CreateInstance(pageType)!;
        SetField(page, "page", 4);
        SetField(page, "loading", true);
        SetField(page, "loadFailed", true);
        SetField(page, "error", "old error");
        var oldVersion = GetField<long>(page, "requestVersion");
        if (page is InstancesPage)
        {
            var cursors = GetField<List<string?>>(page, "pageCursors");
            cursors.AddRange(["page-2", "page-3", "page-4"]);
        }

        Invoke(page, "MarkFiltersDirty");

        Assert.Equal(1, GetField<int>(page, "page"));
        Assert.True(GetField<bool>(page, "filtersDirty"));
        Assert.False(GetField<bool>(page, "loading"));
        Assert.False(GetField<bool>(page, "loadFailed"));
        Assert.Null(GetField<string?>(page, "error"));
        Assert.True(GetField<long>(page, "requestVersion") > oldVersion);
        if (page is InstancesPage)
        {
            Assert.Equal([null], GetField<List<string?>>(page, "pageCursors"));
        }
    }

    [Fact]
    public async Task FailedInstanceSearch_ClearsStaleRowsAndEndsLoadingWithCleanError()
    {
        var page = new InstancesPage();
        SetProperty(page, "Api", Client(new FixedHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":\"Bad variable filter.\"}")));
        SetField(page, "result", new PagedResult<InstanceSummaryDto>([], 2, 50, 12));

        await InvokeAsync(page, "LoadAsync");

        Assert.Null(GetField<PagedResult<InstanceSummaryDto>?>(page, "result"));
        Assert.False(GetField<bool>(page, "loading"));
        Assert.True(GetField<bool>(page, "loadFailed"));
        Assert.Equal("Bad variable filter.", GetField<string?>(page, "error"));
    }

    [Fact]
    public async Task FailedInboxSearch_DoesNotRemainInLoadingState()
    {
        var page = new InboxPage();
        var token = new TokenState();
        token.Set("test-token");
        SetProperty(page, "Token", token);
        SetProperty(page, "Api", Client(new FixedHandler(
            HttpStatusCode.Unauthorized,
            "{\"error\":\"Unauthorized.\"}")));

        await InvokeAsync(page, "LoadAsync");

        Assert.Null(GetField<PagedResult<InboxItemDto>?>(page, "result"));
        Assert.False(GetField<bool>(page, "loading"));
        Assert.True(GetField<bool>(page, "loadFailed"));
        Assert.Equal("Unauthorized.", GetField<string?>(page, "error"));
    }

    [Fact]
    public async Task OlderInstanceResponse_CannotOverwriteNewerSearch()
    {
        var handler = new OrderedInstanceHandler();
        var page = new InstancesPage();
        SetProperty(page, "Api", Client(handler));

        var older = InvokeAsync(page, "LoadAsync");
        await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = InvokeAsync(page, "LoadAsync");
        await newer;
        handler.ReleaseFirstResponse();
        await older;

        var result = GetField<PagedResult<InstanceSummaryDto>?>(page, "result");
        Assert.NotNull(result);
        Assert.Equal(0, result.TotalCount);
        Assert.False(GetField<bool>(page, "loading"));
    }

    [Fact]
    public void DistributionPage_RedirectsOutsideDevelopment()
    {
        var page = new TaskDistributionPage();
        var navigation = new RecordingNavigationManager();
        SetProperty(page, "HostEnvironment", new StubEnvironment("Production"));
        SetProperty(page, "Navigation", navigation);

        Invoke(page, "OnInitialized");

        Assert.Equal("http://localhost/", navigation.LastUri);
    }

    private static WorkflowApiClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://flowbit.test") });

    private static async Task InvokeAsync(object target, string name)
    {
        var task = Assert.IsAssignableFrom<Task>(Invoke(target, name));
        await task;
    }

    private static object? Invoke(object target, string name) =>
        Method(target, name).Invoke(target, null);

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

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{name}' was not found on {target.GetType().Name}.");
        property.SetValue(target, value);
    }

    private sealed class FixedHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Response(statusCode, body));
    }

    private sealed class OrderedInstanceHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> firstResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requestCount;

        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstResponse() => firstResponse.TrySetResult(Response(
            HttpStatusCode.OK,
            "{\"items\":[],\"page\":1,\"pageSize\":50,\"totalCount\":99,\"nextCursor\":null}"));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                FirstRequestStarted.TrySetResult();
                return firstResponse.Task;
            }

            return Task.FromResult(Response(
                HttpStatusCode.OK,
                "{\"items\":[],\"page\":1,\"pageSize\":50,\"totalCount\":0,\"nextCursor\":null}"));
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Flowbit.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingNavigationManager : NavigationManager
    {
        public RecordingNavigationManager() =>
            Initialize("http://localhost/", "http://localhost/task-distribution");

        public string? LastUri { get; private set; }

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            LastUri = ToAbsoluteUri(uri).ToString();

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            LastUri = ToAbsoluteUri(uri).ToString();
    }
}
