extern alias FlowbitUi;

using System.Net;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DevTokenFactory = FlowbitUi::Flowbit.Ui.Auth.DevTokenFactory;
using TokenPage = FlowbitUi::Flowbit.Ui.Components.Pages.Token;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class TestIdentityUiContractTests
{
    [Fact]
    public async Task PageRendersAccessibleCustomClaimEditorAndAllowlistGuidance()
    {
        using var handler = new NoRequestsHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "flowbit-tests-signing-key-at-least-32-bytes-long"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<DevTokenFactory>();
        services.AddSingleton<TokenState>();
        services.AddSingleton(new WorkflowApiClient(http));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TokenPage>();
            return component.ToHtmlString();
        });

        Assert.Contains("Custom claims", html, StringComparison.Ordinal);
        Assert.Contains("id=\"token-claim-name-1\"", html, StringComparison.Ordinal);
        Assert.Contains("for=\"token-claim-name-1\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"token-claim-value-1\"", html, StringComparison.Ordinal);
        Assert.Contains("for=\"token-claim-value-1\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"token-claims-help\"", html, StringComparison.Ordinal);
        Assert.Contains("WorkflowContext:AllowedClaims", html, StringComparison.Ordinal);
        Assert.Contains("[sys.claim.depId]", html, StringComparison.Ordinal);
        Assert.Contains("Add claim", html, StringComparison.Ordinal);
    }

    private sealed class NoRequestsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
