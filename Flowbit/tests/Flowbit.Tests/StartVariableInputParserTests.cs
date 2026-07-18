extern alias FlowbitUi;

using System.Text.Json;
using Flowbit.Shared.Models;
using Xunit;
using StartVariableInputParser = FlowbitUi::Flowbit.Ui.Components.Pages.StartVariableInputParser;

namespace Flowbit.Tests;

public sealed class StartVariableInputParserTests
{
    [Theory]
    [InlineData(WorkflowVariableTypes.String, "hello", JsonValueKind.String)]
    [InlineData(WorkflowVariableTypes.Number, "12.5", JsonValueKind.Number)]
    [InlineData(WorkflowVariableTypes.Boolean, "true", JsonValueKind.True)]
    [InlineData(WorkflowVariableTypes.Date, "2026-07-17", JsonValueKind.String)]
    [InlineData(WorkflowVariableTypes.DateTime, "2026-07-17T14:30:00+03:00", JsonValueKind.String)]
    [InlineData(WorkflowVariableTypes.Json, "{\"approved\":true}", JsonValueKind.Object)]
    public void TryParse_ProducesDeclaredScalarJsonType(string dataType, string raw, JsonValueKind expectedKind)
    {
        var variable = Variable(dataType, false);

        var parsed = StartVariableInputParser.TryParse(variable, raw, out var value, out var error);

        Assert.True(parsed, error);
        Assert.Equal(expectedKind, value.ValueKind);
    }

    [Theory]
    [InlineData(WorkflowVariableTypes.String, "alpha,beta", JsonValueKind.String)]
    [InlineData(WorkflowVariableTypes.Number, "1,2.5", JsonValueKind.Number)]
    [InlineData(WorkflowVariableTypes.Boolean, "true,false", JsonValueKind.True)]
    [InlineData(WorkflowVariableTypes.Date, "2026-07-17,2026-07-18", JsonValueKind.String)]
    [InlineData(WorkflowVariableTypes.DateTime, "2026-07-17T14:30:00Z,2026-07-18T09:00:00+03:00", JsonValueKind.String)]
    public void TryParse_ParsesCommaSeparatedTypedArrays(
        string dataType,
        string raw,
        JsonValueKind expectedFirstKind)
    {
        var parsed = StartVariableInputParser.TryParse(
            Variable(dataType, true), raw, out var value, out var error);

        Assert.True(parsed, error);
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        Assert.Equal(expectedFirstKind, value.EnumerateArray().First().ValueKind);
        Assert.Equal(2, value.GetArrayLength());
    }

    [Fact]
    public void TryParse_AcceptsStrictJsonArrayLiterals()
    {
        var parsed = StartVariableInputParser.TryParse(
            Variable(WorkflowVariableTypes.Number, true),
            "[1,2.5]",
            out var numbers,
            out var numberError);
        Assert.True(parsed, numberError);
        Assert.All(numbers.EnumerateArray(), item => Assert.Equal(JsonValueKind.Number, item.ValueKind));

        parsed = StartVariableInputParser.TryParse(
            Variable(WorkflowVariableTypes.Json, true),
            "[{\"id\":1},null]",
            out var json,
            out var jsonError);
        Assert.True(parsed, jsonError);
        Assert.Equal(JsonValueKind.Object, json[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, json[1].ValueKind);
    }

    [Theory]
    [InlineData(WorkflowVariableTypes.Number, false, "twelve")]
    [InlineData(WorkflowVariableTypes.Boolean, false, "yes")]
    [InlineData(WorkflowVariableTypes.Date, false, "17/07/2026")]
    [InlineData(WorkflowVariableTypes.Date, false, "2026-02-30")]
    [InlineData(WorkflowVariableTypes.DateTime, false, "2026-07-17")]
    [InlineData(WorkflowVariableTypes.DateTime, true, "[\"2026-07-17\"]")]
    [InlineData(WorkflowVariableTypes.Json, false, "{invalid")]
    [InlineData(WorkflowVariableTypes.Number, true, "[1,\"2\"]")]
    [InlineData(WorkflowVariableTypes.Json, true, "one,two")]
    public void TryParse_RejectsValuesThatDoNotMatchTheDeclaration(
        string dataType,
        bool isArray,
        string raw)
    {
        var parsed = StartVariableInputParser.TryParse(
            Variable(dataType, isArray), raw, out _, out var error);

        Assert.False(parsed);
        Assert.Contains(dataType, error, StringComparison.OrdinalIgnoreCase);
    }

    private static VariableModel Variable(string dataType, bool isArray) => new()
    {
        Name = "value",
        DataType = dataType,
        IsArray = isArray
    };
}
