using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class ExampleWorkflowCatalogTests
{
    [Fact]
    public void CatalogContainsEveryWellNamedUniqueExample()
    {
        var paths = ExampleWorkflowData.RelativePaths;
        Assert.NotEmpty(paths);

        var catalogPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "examples",
            "README.md");
        var catalog = File.ReadAllText(catalogPath);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            Assert.Matches(
                new Regex(@"^examples/(?:[a-z0-9-]+/)+\d{2}-[a-z0-9-]+\.json$", RegexOptions.CultureInvariant),
                path);

            using var document = JsonDocument.Parse(ExampleWorkflowData.Read(path));
            var model = JsonSerializer.Deserialize<WorkflowModel>(document.RootElement.GetRawText())
                ?? throw new InvalidOperationException($"Example '{path}' did not deserialize.");

            Assert.False(string.IsNullOrWhiteSpace(model.Id));
            Assert.StartsWith("example-", model.Id, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(model.Name));
            Assert.True(ids.Add(model.Id), $"Duplicate example workflow id '{model.Id}'.");

            var catalogLink = path["examples/".Length..];
            var linkMarker = $"]({catalogLink})";
            Assert.Single(
                Regex.Matches(
                        catalog,
                        Regex.Escape(linkMarker),
                        RegexOptions.CultureInvariant)
                    .Cast<Match>());
        }
    }
}
