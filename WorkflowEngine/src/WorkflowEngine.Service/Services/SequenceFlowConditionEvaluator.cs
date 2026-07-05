using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NCalc;
using NCalc.Exceptions;
using NCalc.Handlers;

namespace WorkflowEngine.Service.Services;

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
///
/// In addition to the built-in NCalc functions (e.g. <c>Min</c>, <c>Max</c>,
/// <c>if</c>, <c>in</c>, math helpers), the following custom helper functions are
/// registered (names are case-insensitive):
///   - <c>Length(s)</c> / <c>Len(s)</c> - character count of a string, or element
///     count of an array variable
///   - <c>IsNullOrEmpty(s)</c>, <c>IsNullOrWhiteSpace(s)</c>
///   - <c>Contains(s, sub)</c> - substring match for strings, or element membership
///     for array variables; both are case-insensitive
///   - <c>StartsWith(s, prefix)</c>, <c>EndsWith(s, suffix)</c>
///   - <c>Lower(s)</c>, <c>Upper(s)</c>, <c>Trim(s)</c>
///   - <c>IsMatch(s, pattern)</c> - regular-expression match (case-insensitive,
///     bounded execution time)
/// Substring and regex matching are case-insensitive. Array variables are exposed
/// as native lists so the collection helpers work directly on them. A helper called
/// with too few arguments is treated as unknown (the expression evaluates to
/// <c>false</c>).
/// </summary>
public static class SequenceFlowConditionEvaluator
{
    private const ExpressionOptions Options =
        ExpressionOptions.CaseInsensitiveStringComparer | ExpressionOptions.AllowNullParameter;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static bool Evaluate(string? condition, IReadOnlyDictionary<string, JsonElement> variables)
    {
        var expression = Normalize(condition);
        if (expression is null)
        {
            return false;
        }

        try
        {
            var ncalc = CreateExpression(expression, variables);
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
            return !CreateExpression(expression, variables: null).HasErrors();
        }
        catch (NCalcException)
        {
            return false;
        }
    }

    /// <summary>
    /// Evaluates an expression and returns the raw typed result (not coerced to a
    /// boolean), for use by scriptTask assignments. Instance variables are exposed
    /// as NCalc parameters (same as <see cref="Evaluate"/>), and the same custom
    /// helper functions are available. Unlike <see cref="Evaluate"/>, a parse or
    /// runtime failure throws a <see cref="WorkflowDomainException"/> naming the
    /// expression so a broken assignment is visible and rolls back the transition
    /// instead of silently writing null. An optional "${ ... }" wrapper is stripped.
    /// </summary>
    public static object? EvaluateValue(string? expression, IReadOnlyDictionary<string, JsonElement> variables)
    {
        var text = Normalize(expression);
        if (text is null)
        {
            return null;
        }

        try
        {
            var ncalc = CreateExpression(text, variables);
            return ncalc.Evaluate();
        }
        catch (NCalcException ex)
        {
            throw new WorkflowDomainException(
                $"Expression '{expression}' could not be evaluated: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new WorkflowDomainException(
                $"Expression '{expression}' could not be evaluated: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            throw new WorkflowDomainException(
                $"Expression '{expression}' could not be evaluated: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds an <see cref="Expression"/> with the shared options, the supplied
    /// instance variables as parameters (when given), and the custom helper
    /// functions registered. Both <see cref="Evaluate"/> and <see cref="IsValid"/>
    /// go through here so runtime evaluation and author-time validation stay in
    /// sync.
    /// </summary>
    private static Expression CreateExpression(
        string expression,
        IReadOnlyDictionary<string, JsonElement>? variables)
    {
        var ncalc = new Expression(expression, Options);
        if (variables is not null)
        {
            ncalc.Parameters = BuildParameters(variables);
        }

        ncalc.EvaluateFunction += EvaluateCustomFunction;
        return ncalc;
    }

    /// <summary>
    /// Implements the custom helper functions. Unknown names (or calls with too
    /// few arguments) are left unhandled so NCalc raises a caught exception and
    /// the expression degrades to <c>false</c>.
    /// </summary>
    private static void EvaluateCustomFunction(string name, FunctionEventArgs args)
    {
        var arguments = args.Parameters;
        switch (name.ToLowerInvariant())
        {
            case "length":
            case "len":
                if (arguments.Count < 1) return;
                args.Result = GetLength(arguments.Evaluate(0));
                return;
            case "isnullorempty":
                if (arguments.Count < 1) return;
                args.Result = string.IsNullOrEmpty(AsString(arguments.Evaluate(0)));
                return;
            case "isnullorwhitespace":
                if (arguments.Count < 1) return;
                args.Result = string.IsNullOrWhiteSpace(AsString(arguments.Evaluate(0)));
                return;
            case "contains":
                if (arguments.Count < 2) return;
                args.Result = ContainsValue(arguments.Evaluate(0), arguments.Evaluate(1));
                return;
            case "startswith":
                if (arguments.Count < 2) return;
                args.Result = AsString(arguments.Evaluate(0))
                    .StartsWith(AsString(arguments.Evaluate(1)), StringComparison.OrdinalIgnoreCase);
                return;
            case "endswith":
                if (arguments.Count < 2) return;
                args.Result = AsString(arguments.Evaluate(0))
                    .EndsWith(AsString(arguments.Evaluate(1)), StringComparison.OrdinalIgnoreCase);
                return;
            case "lower":
                if (arguments.Count < 1) return;
                args.Result = AsString(arguments.Evaluate(0)).ToLowerInvariant();
                return;
            case "upper":
                if (arguments.Count < 1) return;
                args.Result = AsString(arguments.Evaluate(0)).ToUpperInvariant();
                return;
            case "trim":
                if (arguments.Count < 1) return;
                args.Result = AsString(arguments.Evaluate(0)).Trim();
                return;
            case "ismatch":
                if (arguments.Count < 2) return;
                args.Result = TryRegexMatch(
                    AsString(arguments.Evaluate(0)),
                    AsString(arguments.Evaluate(1)));
                return;
        }
    }

    private static string AsString(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static long GetLength(object? value)
    {
        if (value is string s)
        {
            return s.Length;
        }

        if (value is IEnumerable enumerable and not string)
        {
            if (value is ICollection collection)
            {
                return collection.Count;
            }

            return enumerable.Cast<object?>().LongCount();
        }

        return AsString(value).Length;
    }

    private static bool ContainsValue(object? container, object? search)
    {
        if (container is string s)
        {
            return s.Contains(AsString(search), StringComparison.OrdinalIgnoreCase);
        }

        if (container is IEnumerable enumerable and not string)
        {
            var searchText = AsString(search);
            foreach (var item in enumerable)
            {
                if (AsString(item).Equals(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        return AsString(container).Contains(AsString(search), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRegexMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
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
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertValue).ToList<object?>(),
        _ => element.GetRawText()
    };

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrWhiteSpace(s),
        sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,
        IEnumerable enumerable and not string => enumerable.Cast<object?>().Any(),
        _ => true
    };
}
