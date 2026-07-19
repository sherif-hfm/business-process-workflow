using System.Globalization;
using System.Text.Json;
using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

public sealed class SequenceFlowConditionEvaluatorTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyVariables =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Evaluate_TreatsLargeFiniteNumbersAsTruthyWithoutOverflow()
    {
        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["large"] = JsonSerializer.SerializeToElement(1e308)
        };

        Assert.True(SequenceFlowConditionEvaluator.Evaluate("large", variables));
    }

    [Theory]
    [InlineData("Pow(10, 10000)")]
    [InlineData("Sqrt(-1)")]
    public void Evaluate_NonFiniteOrOverflowingNumericResultsFailClosed(string condition)
    {
        var exception = Record.Exception(() =>
            Assert.False(SequenceFlowConditionEvaluator.Evaluate(condition, EmptyVariables)));

        Assert.Null(exception);
    }

    [Fact]
    public void EvaluateCompletion_ArithmeticFailuresFailClosed()
    {
        var exception = Record.Exception(() =>
            Assert.False(SequenceFlowConditionEvaluator.EvaluateCompletion(
                "Pow(10, 10000)",
                EmptyVariables,
                new Dictionary<int, int>(),
                0)));

        Assert.Null(exception);
    }

    [Fact]
    public void Evaluate_StringTruthinessRequiresNonWhitespaceText()
    {
        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["blank"] = JsonSerializer.SerializeToElement("   "),
            ["text"] = JsonSerializer.SerializeToElement(" yes ")
        };

        Assert.False(SequenceFlowConditionEvaluator.Evaluate("blank", variables));
        Assert.True(SequenceFlowConditionEvaluator.Evaluate("text", variables));
    }

    [Fact]
    public void IsMatch_IsCaseInsensitiveAndCultureInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(SequenceFlowConditionEvaluator.Evaluate(
                "IsMatch('I', '^i$')",
                EmptyVariables));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
