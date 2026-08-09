extern alias FlowbitUi;

using System.Net;
using System.Text.RegularExpressions;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkflowAttributes = FlowbitUi::Flowbit.Ui.Components.Shared.WorkflowAttributes;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowAttributesComponentTests
{
    [Fact]
    public async Task RendersOrderedSemanticPairsWithoutChangingValueText()
    {
        var html = await RenderAsync(
            new WorkflowAttributeModel { Key = "form", Value = "purchase-request" },
            new WorkflowAttributeModel { Key = "notes", Value = " first line \n  <unsafe>&second " });

        AssertDisclosureOpenState(html, expectedOpen: true);
        Assert.Contains("<dl", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Node attributes key-value pairs\"", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf(">form</dt>", StringComparison.Ordinal)
            < html.IndexOf(">notes</dt>", StringComparison.Ordinal));
        Assert.Contains(">purchase-request</dd>", html, StringComparison.Ordinal);
        Assert.Contains(
            " first line \n  <unsafe>&second ",
            WebUtility.HtmlDecode(html),
            StringComparison.Ordinal);
        Assert.DoesNotContain("<unsafe>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;unsafe&gt;&amp;second", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyAttributesRenderNoDisclosure()
    {
        var html = await RenderAsync();

        Assert.Equal(string.Empty, html);
    }

    [Fact]
    public async Task ClosedStateOmitsOpenAttribute()
    {
        var html = await RenderAsync(
            open: false,
            new WorkflowAttributeModel { Key = "region", Value = "riyadh" });

        AssertDisclosureOpenState(html, expectedOpen: false);
    }

    private static Task<string> RenderAsync(params WorkflowAttributeModel[] attributes) =>
        RenderAsync(open: true, attributes);

    private static async Task<string> RenderAsync(
        bool open,
        params WorkflowAttributeModel[] attributes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider,
            provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowAttributes>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(WorkflowAttributes.Attributes)] = attributes,
                    [nameof(WorkflowAttributes.Label)] = "Node attributes",
                    [nameof(WorkflowAttributes.Open)] = open
                }));

            return component.ToHtmlString();
        });
    }

    private static void AssertDisclosureOpenState(string html, bool expectedOpen)
    {
        var start = html.IndexOf("<details", StringComparison.Ordinal);
        Assert.True(start >= 0, "The rendered markup did not contain a details element.");
        var end = html.IndexOf('>', start);
        Assert.True(end > start, "The rendered details start tag was malformed.");
        var startTag = html[start..(end + 1)];
        var hasOpenAttribute = Regex.IsMatch(
            startTag,
            @"\sopen(?:\s|=|>)",
            RegexOptions.CultureInvariant);

        Assert.Equal(expectedOpen, hasOpenAttribute);
    }
}
