extern alias FlowbitUi;

using System.Text.Json;
using AdvancedVariableFilterInput = FlowbitUi::Flowbit.Ui.Components.Shared.AdvancedVariableFilterInput;
using Xunit;

namespace Flowbit.Tests;

public sealed class AdvancedVariableFilterInputTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_TreatsEmptyInputAsNoFilter(string? input)
    {
        var parsed = AdvancedVariableFilterInput.TryParse(input, out var filter, out var error);

        Assert.True(parsed, error);
        Assert.Null(filter);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_ClonesNestedUnicodeFilterBeyondDocumentLifetime()
    {
        const string input = """
            {
              "$and": [
                { "request.medicalCenter.id": { "$eq": "مركز-١٠٤٢" } },
                { "request.services": { "$contains": "health-certificate" } }
              ]
            }
            """;

        var parsed = AdvancedVariableFilterInput.TryParse(input, out var filter, out var error);

        Assert.True(parsed, error);
        Assert.Equal(JsonValueKind.Object, filter!.Value.ValueKind);
        Assert.Equal(
            "مركز-١٠٤٢",
            filter.Value.GetProperty("$and")[0]
                .GetProperty("request.medicalCenter.id")
                .GetProperty("$eq")
                .GetString());
    }

    [Fact]
    public void TryFormat_ProducesIndentedJsonWithoutChangingValues()
    {
        var formatted = AdvancedVariableFilterInput.TryFormat(
            "{\"count\":{\"$gte\":12.5}}",
            out var output,
            out var error);

        Assert.True(formatted, error);
        Assert.Contains(Environment.NewLine, output, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(output);
        Assert.Equal(12.5m, document.RootElement.GetProperty("count").GetProperty("$gte").GetDecimal());
    }

    [Theory]
    [InlineData("[")]
    [InlineData("{invalid}")]
    public void TryParse_RejectsMalformedJson(string input)
    {
        var parsed = AdvancedVariableFilterInput.TryParse(input, out var filter, out var error);

        Assert.False(parsed);
        Assert.Null(filter);
        Assert.Contains("not valid JSON", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"medical-center\"")]
    public void TryParse_RequiresAnObjectRoot(string input)
    {
        var parsed = AdvancedVariableFilterInput.TryParse(input, out var filter, out var error);

        Assert.False(parsed);
        Assert.Null(filter);
        Assert.Contains("JSON object", error, StringComparison.OrdinalIgnoreCase);
    }
}
