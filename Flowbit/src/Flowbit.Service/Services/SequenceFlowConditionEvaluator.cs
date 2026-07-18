using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Service.Models;
using NCalc;
using NCalc.Exceptions;
using NCalc.Handlers;

namespace Flowbit.Service.Services;

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

    private static readonly HashSet<string> SupportedFlowInfoPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "actions.count",
        "actions.last.user",
        "actions.last.userRoles",
        "actions.last.occurredAt",
        "actions.last.kind",
        "actions.last.values",
        "traversals.count",
        "traversals.last.user",
        "traversals.last.userRoles",
        "traversals.last.occurredAt",
        "traversals.last.kind",
        "traversals.last.values",
        "all"
    };

    public static bool Evaluate(
        string? condition,
        IReadOnlyDictionary<string, JsonElement> variables,
        SequenceFlowInfoSnapshot? flowInfo = null)
    {
        var expression = Normalize(condition);
        if (expression is null)
        {
            return false;
        }

        try
        {
            var ncalc = CreateExpression(expression, variables, flowInfo: flowInfo);
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
    /// Evaluates a multi-instance aggregate completion condition. CountFlow(id)
    /// and PercentFlow(id) read the transactionally maintained outcome counters.
    /// </summary>
    public static bool EvaluateCompletion(
        string? condition,
        IReadOnlyDictionary<string, JsonElement> variables,
        IReadOnlyDictionary<int, int> flowCounts,
        int totalCount,
        SequenceFlowInfoSnapshot? flowInfo = null)
    {
        var expression = Normalize(condition);
        if (expression is null)
        {
            return false;
        }

        try
        {
            var ncalc = CreateExpression(expression, variables, flowCounts, totalCount, flowInfo);
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
    /// Set preserveComplexTypes when callers must distinguish objects from strings.
    /// </summary>
    public static object? EvaluateValue(
        string? expression,
        IReadOnlyDictionary<string, JsonElement> variables,
        bool preserveComplexTypes = false,
        SequenceFlowInfoSnapshot? flowInfo = null)
    {
        var text = Normalize(expression);
        if (text is null)
        {
            return null;
        }

        try
        {
            var ncalc = CreateExpression(
                text,
                variables,
                preserveComplexTypes: preserveComplexTypes,
                flowInfo: flowInfo);
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
        IReadOnlyDictionary<string, JsonElement>? variables,
        IReadOnlyDictionary<int, int>? flowCounts = null,
        int totalCount = 0,
        SequenceFlowInfoSnapshot? flowInfo = null,
        bool preserveComplexTypes = false)
    {
        var ncalc = new Expression(expression, Options);
        if (variables is not null)
        {
            ncalc.Parameters = BuildParameters(variables, preserveComplexTypes);
        }

        ncalc.EvaluateFunction += (name, args) =>
        {
            if (flowCounts is not null && TryEvaluateMultiInstanceFunction(name, args, flowCounts, totalCount))
            {
                return;
            }
            if (flowInfo is not null && TryEvaluateFlowInfoFunction(name, args, flowInfo))
            {
                return;
            }
            EvaluateCustomFunction(name, args);
        };
        return ncalc;
    }

    private static bool TryEvaluateFlowInfoFunction(
        string name,
        FunctionEventArgs args,
        SequenceFlowInfoSnapshot flowInfo)
    {
        if (!name.Equals("FlowInfo", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Parameters.Count != 2)
        {
            return false;
        }

        var rawFlowId = args.Parameters.Evaluate(0);
        if (!int.TryParse(
                Convert.ToString(rawFlowId, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var flowId))
        {
            return false;
        }

        var path = Convert.ToString(args.Parameters.Evaluate(1), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(path) || !SupportedFlowInfoPaths.Contains(path))
        {
            return false;
        }

        if (!flowInfo.TryGetSummary(flowId, out var summary))
        {
            throw new InvalidOperationException(
                $"FlowInfo references sequence flow #{flowId}, which is not part of this workflow definition.");
        }

        args.Result = ResolveFlowInfoPath(summary, path);
        return true;
    }

    private static object? ResolveFlowInfoPath(SequenceFlowRuntimeSummary summary, string path)
    {
        if (path.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return summary.ToJsonElement();
        }

        var view = path.StartsWith("actions.", StringComparison.OrdinalIgnoreCase)
            ? summary.Actions
            : summary.Traversals;

        if (path.EndsWith(".count", StringComparison.OrdinalIgnoreCase))
        {
            return view.Count;
        }

        var last = view.Last;
        if (last is null)
        {
            return null;
        }

        if (path.EndsWith(".user", StringComparison.OrdinalIgnoreCase)) return last.User;
        if (path.EndsWith(".userRoles", StringComparison.OrdinalIgnoreCase)) return last.UserRoles;
        if (path.EndsWith(".occurredAt", StringComparison.OrdinalIgnoreCase))
        {
            return last.OccurredAt.ToString("O", CultureInfo.InvariantCulture);
        }
        if (path.EndsWith(".kind", StringComparison.OrdinalIgnoreCase)) return last.Kind;
        if (path.EndsWith(".values", StringComparison.OrdinalIgnoreCase)) return last.Values?.Clone();

        return null;
    }

    /// <summary>
    /// Fast, case-insensitive detection used to avoid loading and maintaining
    /// FlowInfo state for definitions that do not reference the function.
    /// Text inside quoted string literals is ignored.
    /// </summary>
    public static bool ContainsFlowInfoReference(string? expression) =>
        FlowInfoExpressionInspector.ContainsReference(Normalize(expression));

    /// <summary>
    /// Performs the semantic checks that NCalc's grammar parser cannot: FlowInfo
    /// must be legal in the expression's context and must receive a literal known
    /// flow id and a literal supported property path.
    /// </summary>
    public static bool TryValidateFlowInfoReferences(
        string? expression,
        IReadOnlySet<int> knownFlowIds,
        bool allowed,
        out string? error) =>
        FlowInfoExpressionInspector.TryValidate(
            Normalize(expression),
            knownFlowIds,
            allowed,
            SupportedFlowInfoPaths,
            out error);

    private static bool TryEvaluateMultiInstanceFunction(
        string name,
        FunctionEventArgs args,
        IReadOnlyDictionary<int, int> flowCounts,
        int totalCount)
    {
        if (!name.Equals("CountFlow", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("PercentFlow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Parameters.Count < 1)
        {
            return false;
        }

        var raw = args.Parameters.Evaluate(0);
        if (!int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var flowId))
        {
            return false;
        }

        var count = flowCounts.GetValueOrDefault(flowId);
        args.Result = name.Equals("CountFlow", StringComparison.OrdinalIgnoreCase)
            ? count
            : totalCount <= 0 ? 0d : count * 100d / totalCount;
        return true;
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
        IReadOnlyDictionary<string, JsonElement> variables,
        bool preserveComplexTypes)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in variables)
        {
            parameters[pair.Key] = ConvertValue(pair.Value, preserveComplexTypes);
        }

        return parameters;
    }

    private static object? ConvertValue(JsonElement element, bool preserveComplexTypes) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => element.EnumerateArray()
            .Select(item => ConvertValue(item, preserveComplexTypes)).ToList<object?>(),
        JsonValueKind.Object when preserveComplexTypes => element.Clone(),
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

internal static class FlowInfoExpressionInspector
{
    public static bool ContainsReference(string? expression)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return false;
        }

        for (var index = 0; index < expression.Length;)
        {
            if (IsQuote(expression[index]))
            {
                SkipQuoted(expression, ref index);
                continue;
            }

            if (!IsIdentifierStart(expression[index]))
            {
                index++;
                continue;
            }

            var start = index++;
            while (index < expression.Length && IsIdentifierPart(expression[index])) index++;
            if (!expression.AsSpan(start, index - start).Equals("FlowInfo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var next = SkipWhitespace(expression, index);
            if (next < expression.Length && expression[next] == '(')
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryValidate(
        string? expression,
        IReadOnlySet<int> knownFlowIds,
        bool allowed,
        IReadOnlySet<string> supportedPaths,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(knownFlowIds);
        ArgumentNullException.ThrowIfNull(supportedPaths);

        error = null;
        if (string.IsNullOrEmpty(expression))
        {
            return true;
        }

        for (var index = 0; index < expression.Length;)
        {
            if (IsQuote(expression[index]))
            {
                SkipQuoted(expression, ref index);
                continue;
            }

            if (!IsIdentifierStart(expression[index]))
            {
                index++;
                continue;
            }

            var identifierStart = index++;
            while (index < expression.Length && IsIdentifierPart(expression[index])) index++;
            if (!expression.AsSpan(identifierStart, index - identifierStart)
                    .Equals("FlowInfo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var openParenthesis = SkipWhitespace(expression, index);
            if (openParenthesis >= expression.Length || expression[openParenthesis] != '(')
            {
                continue;
            }

            if (!allowed)
            {
                error = "FlowInfo is not available in this expression context.";
                return false;
            }

            if (!TryReadArguments(expression, openParenthesis, out var arguments, out var closeParenthesis))
            {
                error = "FlowInfo has an unterminated argument list.";
                return false;
            }

            if (arguments.Count != 2)
            {
                error = "FlowInfo requires exactly two arguments: a literal flow id and a literal property path.";
                return false;
            }

            var flowIdText = arguments[0].Trim();
            if (!int.TryParse(
                    flowIdText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var flowId)
                || !IsIntegerLiteral(flowIdText))
            {
                error = "FlowInfo's first argument must be a literal integer flow id.";
                return false;
            }

            if (!knownFlowIds.Contains(flowId))
            {
                error = $"FlowInfo references unknown sequence flow #{flowId}.";
                return false;
            }

            if (!TryReadStringLiteral(arguments[1].Trim(), out var path))
            {
                error = "FlowInfo's second argument must be a literal property path string.";
                return false;
            }

            if (!supportedPaths.Contains(path))
            {
                error = $"FlowInfo property path '{path}' is not supported.";
                return false;
            }

            index = closeParenthesis + 1;
        }

        return true;
    }

    private static bool TryReadArguments(
        string expression,
        int openParenthesis,
        out List<string> arguments,
        out int closeParenthesis)
    {
        arguments = [];
        closeParenthesis = -1;
        var argumentStart = openParenthesis + 1;
        var depth = 0;

        for (var index = argumentStart; index < expression.Length; index++)
        {
            if (IsQuote(expression[index]))
            {
                SkipQuoted(expression, ref index);
                index--;
                continue;
            }

            switch (expression[index])
            {
                case '(':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case ')' when depth == 0:
                    arguments.Add(expression[argumentStart..index]);
                    closeParenthesis = index;
                    return true;
                case ',' when depth == 0:
                    arguments.Add(expression[argumentStart..index]);
                    argumentStart = index + 1;
                    break;
            }
        }

        return false;
    }

    private static bool TryReadStringLiteral(string value, out string result)
    {
        result = string.Empty;
        if (value.Length < 2 || !IsQuote(value[0]) || value[^1] != value[0])
        {
            return false;
        }

        // Supported property paths contain only letters and dots, so escape
        // sequences or embedded quotes cannot be part of a valid literal.
        var content = value[1..^1];
        if (content.IndexOf(value[0]) >= 0 || content.IndexOf('\\') >= 0)
        {
            return false;
        }

        result = content;
        return true;
    }

    private static bool IsIntegerLiteral(string value)
    {
        if (value.Length == 0) return false;
        var index = value[0] is '+' or '-' ? 1 : 0;
        if (index == value.Length) return false;
        for (; index < value.Length; index++)
        {
            if (!char.IsAsciiDigit(value[index])) return false;
        }
        return true;
    }

    private static void SkipQuoted(string expression, ref int index)
    {
        var quote = expression[index++];
        while (index < expression.Length)
        {
            if (expression[index] == '\\')
            {
                index = Math.Min(index + 2, expression.Length);
                continue;
            }

            if (expression[index] != quote)
            {
                index++;
                continue;
            }

            // Accept doubled quotes as an escaped quote while scanning. NCalc's
            // grammar validation remains authoritative for whether that spelling
            // is legal in an actual expression.
            if (index + 1 < expression.Length && expression[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            index++;
            return;
        }
    }

    private static int SkipWhitespace(string expression, int index)
    {
        while (index < expression.Length && char.IsWhiteSpace(expression[index])) index++;
        return index;
    }

    private static bool IsQuote(char value) => value is '\'' or '"';
    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';
    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';
}
