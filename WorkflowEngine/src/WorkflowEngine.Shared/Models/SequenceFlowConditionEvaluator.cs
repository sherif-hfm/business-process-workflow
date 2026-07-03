using System.Globalization;
using System.Text.Json;
using NCalc;
using NCalc.Exceptions;

namespace WorkflowEngine.Shared.Models;

/// <summary>
/// Evaluates condition expressions on exclusive-gateway outgoing flows using
/// <see href="https://github.com/ncalc/ncalc">NCalc</see>.
///
/// Supports the full NCalc expression grammar, e.g.:
///   - comparisons: "amount > 1000", "status == 'approved'"
///   - boolean logic: "amount > 1000 and region == 'EU'", "a or b"
///   - arithmetic and parentheses: "(qty * price) >= 500"
///   - bare variable truthiness: "approved"
///
/// Instance variables are exposed as NCalc parameters (case-insensitive names).
/// String comparisons are case-insensitive to match the historical behaviour.
/// The result is coerced to a boolean (non-zero numbers and non-empty strings
/// are truthy), so a bare variable name still works as a truthiness check.
/// An optional "${ ... }" wrapper is stripped for backward compatibility.
/// Invalid or unresolvable expressions evaluate to <c>false</c>.
/// </summary>
public static class SequenceFlowConditionEvaluator
{
    private const ExpressionOptions Options =
        ExpressionOptions.CaseInsensitiveStringComparer | ExpressionOptions.AllowNullParameter;

    public static bool Evaluate(string? condition, IReadOnlyDictionary<string, JsonElement> variables)
    {
        var expression = Normalize(condition);
        if (expression is null)
        {
            return false;
        }

        try
        {
            var ncalc = new Expression(expression, Options)
            {
                Parameters = BuildParameters(variables)
            };

            return IsTruthy(ncalc.Evaluate());
        }
        catch (NCalcException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the expression parses successfully. Used for
    /// author-time validation of gateway conditions.
    /// </summary>
    public static bool IsValid(string? condition)
    {
        var expression = Normalize(condition);
        if (expression is null)
        {
            return false;
        }

        try
        {
            return !new Expression(expression, Options).HasErrors();
        }
        catch (NCalcException)
        {
            return false;
        }
    }

    private static string? Normalize(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return null;
        }

        var expression = condition.Trim();
        if (expression.StartsWith("${", StringComparison.Ordinal) && expression.EndsWith('}'))
        {
            expression = expression[2..^1].Trim();
        }

        return string.IsNullOrWhiteSpace(expression) ? null : expression;
    }

    private static Dictionary<string, object?> BuildParameters(
        IReadOnlyDictionary<string, JsonElement> variables)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in variables)
        {
            parameters[pair.Key] = ConvertValue(pair.Value);
        }

        return parameters;
    }

    private static object? ConvertValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText()
    };

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrWhiteSpace(s),
        sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,
        _ => true
    };
}
