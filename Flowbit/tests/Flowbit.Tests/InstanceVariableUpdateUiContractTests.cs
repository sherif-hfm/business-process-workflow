extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using InstanceDetailPage = FlowbitUi::Flowbit.Ui.Components.Pages.InstanceDetail;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class InstanceVariableUpdateUiContractTests
{
    [Fact]
    public async Task InstanceDetailRendersAdministrativeAuditAndBatchReopenLink()
    {
        var instance = InstanceWithAdministrativeUpdate();
        using var handler = new InstanceHandler(instance);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowApiClient(http));
        services.AddSingleton(new TokenState());
        services.AddSingleton<NavigationManager>(new StubNavigationManager());
        services.AddSingleton<IJSRuntime>(new StubJsRuntime());
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<InstanceDetailPage>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(InstanceDetailPage.InstanceId)] = 42L
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("/api/instances/42", handler.Paths);
        Assert.Contains("/api/instances/42/flows", handler.Paths);
        Assert.Contains("id=\"variable-updates\"", html, StringComparison.Ordinal);
        Assert.Contains("Administrative variable updates", html, StringComparison.Ordinal);
        Assert.Contains("Operation #81", html, StringComparison.Ordinal);
        Assert.Contains("Correct imported approval state", html, StringComparison.Ordinal);
        Assert.Contains("reviewContext", html, StringComparison.Ordinal);
        Assert.Contains("href=\"instance-variable-updates?batchId=91\"", html, StringComparison.Ordinal);
        Assert.Contains("Batch #91", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#variable-updates\"", html, StringComparison.Ordinal);
    }

    private static InstanceDetailDto InstanceWithAdministrativeUpdate()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var value = JsonSerializer.SerializeToElement(new { approved = true });
        var outcome = new InstanceVariableUpdateOutcomeDto("reviewContext", "updated", 501, value);
        var audit = new InstanceVariableUpdateAuditDto(
            81,
            42,
            17,
            "operator",
            ["admin"],
            "Correct imported approval state",
            [outcome],
            now,
            "direct-retry",
            91,
            701);
        var workflow = new WorkflowDetailDto(
            17,
            "Purchase request",
            "purchase-request",
            2,
            true,
            false,
            now.AddDays(-1),
            new WorkflowModel { Id = "purchase-request", Name = "Purchase request" });
        var variable = new InstanceVariableDto(
            501,
            "reviewContext",
            null,
            "operator",
            value,
            now)
        {
            InstanceVariableUpdateAuditId = 81
        };

        return new InstanceDetailDto(
            42,
            workflow,
            0,
            "Completed",
            null,
            "completed",
            "PR-42",
            null,
            "starter",
            now.AddHours(-1),
            now,
            [variable],
            [],
            null,
            null,
            null)
        {
            VariableUpdates = [audit]
        };
    }

    private sealed class InstanceHandler(InstanceDetailDto instance) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            Paths.Add(path);
            var response = path switch
            {
                "/api/instances/42" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(instance) },
                "/api/instances/42/flows" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<SequenceFlowModel>()) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager() => Initialize("https://flowbit.test/", "https://flowbit.test/instances/42");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }

    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Flowbit.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
