extern alias FlowbitUi;

using System.Text.Json;
using InstanceVariableUpdateInputParser = FlowbitUi::Flowbit.Ui.Components.Pages.InstanceVariableUpdateInputParser;
using Xunit;

namespace Flowbit.Tests;

public sealed class InstanceVariableUpdateInputParserTests
{
    [Theory]
    [InlineData("null", JsonValueKind.Null, "null")]
    [InlineData("true", JsonValueKind.True, "boolean")]
    [InlineData("12.50", JsonValueKind.Number, "number")]
    [InlineData("\"approved\"", JsonValueKind.String, "string")]
    [InlineData("[1,2]", JsonValueKind.Array, "array")]
    [InlineData("{\"approved\":true}", JsonValueKind.Object, "object")]
    public void TryParseValue_AcceptsEveryJsonKind(string raw, JsonValueKind expectedKind, string expectedLabel)
    {
        var parsed = InstanceVariableUpdateInputParser.TryParseValue(raw, out var value, out var kind, out var error);

        Assert.True(parsed, error);
        Assert.Equal(expectedKind, value.ValueKind);
        Assert.Equal(expectedLabel, kind);
    }

    [Fact]
    public void TryParseValue_RejectsTrailingOrIncompleteJson()
    {
        Assert.False(InstanceVariableUpdateInputParser.TryParseValue("true false", out _, out _, out var trailing));
        Assert.False(InstanceVariableUpdateInputParser.TryParseValue("{", out _, out _, out var incomplete));
        Assert.NotNull(trailing);
        Assert.NotNull(incomplete);
    }

    [Theory]
    [InlineData("sys.internal")]
    [InlineData(" CONFIG.value ")]
    [InlineData("Setting.locale")]
    [InlineData("MI.item")]
    public void ValidateName_RejectsReservedPrefixesCaseInsensitively(string name)
    {
        Assert.NotNull(InstanceVariableUpdateInputParser.ValidateName(name));
    }

    [Fact]
    public void FindDuplicateNameErrors_UsesTrimmedCaseInsensitiveNames()
    {
        var errors = InstanceVariableUpdateInputParser.FindDuplicateNameErrors([" amount ", "AMOUNT", "currency"]);

        Assert.Equal(2, errors.Count);
        Assert.Contains(0, errors.Keys);
        Assert.Contains(1, errors.Keys);
    }
}
