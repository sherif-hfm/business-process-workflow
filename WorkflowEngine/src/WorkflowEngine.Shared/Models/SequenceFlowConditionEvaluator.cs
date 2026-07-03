using System.Globalization;
using System.Text.Json;

namespace WorkflowEngine.Shared.Models;

/// <summary>
/// Minimal condition language for exclusive-gateway outgoing flows.
/// Supported forms:
///   - "variable op literal" where op is one of == != &lt; &lt;= &gt; &gt;=
///   - bare "variable" (true when the variable is a truthy value)
/// Literals may be numbers, true/false, or (optionally quoted) strings.
/// Comparisons are numeric when both sides parse as numbers, otherwise
/// case-insensitive string comparisons (only == and != are meaningful there).
/// </summary>
public static class SequenceFlowConditionEvaluator
{
    private static readonly string[] Operators = ["==", "!=", "<=", ">=", "<", ">"];

    public static bool Evaluate(string? condition, IReadOnlyDictionary<string, JsonElement> variables)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return false;
        }

        var expression = condition.Trim();
        if (expression.StartsWith("${", StringComparison.Ordinal) && expression.EndsWith('}'))
        {
            expression = expression[2..^1].Trim();
        }

        foreach (var op in Operators)
        {
            var index = expression.IndexOf(op, StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            var left = expression[..index].Trim();
            var right = expression[(index + op.Length)..].Trim();
            return Compare(op, ResolveValue(left, variables), ResolveLiteral(right));
        }

        // Bare variable => truthiness.
        return IsTruthy(ResolveValue(expression, variables));
    }

    private static bool Compare(string op, string? left, string? right)
    {
        var numericLeft = TryParseNumber(left, out var leftNumber);
        var numericRight = TryParseNumber(right, out var rightNumber);

        if (numericLeft && numericRight)
        {
            var cmp = leftNumber.CompareTo(rightNumber);
            return op switch
            {
                "==" => cmp == 0,
                "!=" => cmp != 0,
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                ">=" => cmp >= 0,
                _ => false
            };
        }

        var stringCmp = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            "==" => stringCmp == 0,
            "!=" => stringCmp != 0,
            "<" => stringCmp < 0,
            "<=" => stringCmp <= 0,
            ">" => stringCmp > 0,
            ">=" => stringCmp >= 0,
            _ => false
        };
    }

    private static string? ResolveValue(string token, IReadOnlyDictionary<string, JsonElement> variables)
    {
        foreach (var pair in variables)
        {
            if (string.Equals(pair.Key, token, StringComparison.OrdinalIgnoreCase))
            {
                return JsonElementToString(pair.Value);
            }
        }

        // Not a known variable: treat as a literal.
        return ResolveLiteral(token);
    }

    private static string ResolveLiteral(string token)
    {
        if (token.Length >= 2
            && ((token[0] == '"' && token[^1] == '"') || (token[0] == '\'' && token[^1] == '\'')))
        {
            return token[1..^1];
        }

        return token;
    }

    private static string? JsonElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText()
    };

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var boolean))
        {
            return boolean;
        }

        if (TryParseNumber(value, out var number))
        {
            return number != 0;
        }

        return true;
    }

    private static bool TryParseNumber(string? value, out double number)
    {
        number = 0;
        return !string.IsNullOrWhiteSpace(value)
            && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number);
    }
}
