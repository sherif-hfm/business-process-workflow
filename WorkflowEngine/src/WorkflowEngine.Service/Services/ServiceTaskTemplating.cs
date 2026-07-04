using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkflowEngine.Service.Services;

/// <summary>
/// Substitutes <c>${var}</c> placeholders in service-task URLs, header values,
/// and JSON body templates using the instance variables, and extracts values
/// from a JSON response body via dotted paths (e.g. <c>a.b.0.c</c>).
///
/// - In URLs and header values a placeholder is replaced by the variable's
///   scalar text (a string keeps its raw text; numbers/booleans use their JSON
///   text; missing variables become an empty string).
/// - In a JSON body substitution is quote-aware: a placeholder that sits inside a
///   JSON string literal is replaced by the variable's <em>escaped scalar text</em>
///   (no surrounding quotes), so <c>"Hi ${user}"</c> becomes <c>"Hi alice"</c>;
///   a placeholder in a bare value position is replaced by the variable's JSON
///   representation (<see cref="JsonElement.GetRawText"/>), so <c>${amount}</c>
///   stays an unquoted number/boolean/object. A missing variable becomes an empty
///   string inside a string and <c>null</c> in a bare position.
/// </summary>
public static partial class ServiceTaskTemplating
{
    [GeneratedRegex(@"\$\{\s*([^}\s]+)\s*\}")]
    private static partial Regex PlaceholderRegex();

    public static string SubstituteScalar(
        string? template,
        IReadOnlyDictionary<string, JsonElement> variables)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template ?? string.Empty;
        }

        return PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            return variables.TryGetValue(name, out var value) ? ToScalarText(value) : string.Empty;
        });
    }

    public static string? SubstituteJson(
        string? template,
        IReadOnlyDictionary<string, JsonElement> variables)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        var result = new StringBuilder(template.Length);
        var inString = false;

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            if (c == '$' && TryReadPlaceholder(template, i, out var name, out var end))
            {
                var has = variables.TryGetValue(name, out var value);
                if (inString)
                {
                    // Inside a JSON string literal: emit escaped scalar text (no quotes).
                    AppendJsonEscaped(result, has ? ToScalarText(value) : string.Empty);
                }
                else
                {
                    // Bare value position: emit the variable's JSON representation.
                    result.Append(has ? value.GetRawText() : "null");
                }

                i = end;
                continue;
            }

            if (c == '"' && !IsEscaped(template, i))
            {
                inString = !inString;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    // Matches the same shape as PlaceholderRegex ("${" optional-ws name optional-ws "}"),
    // where name has no whitespace or "}". Returns the trimmed name and the index of "}".
    private static bool TryReadPlaceholder(string template, int start, out string name, out int end)
    {
        name = string.Empty;
        end = start;
        if (start + 1 >= template.Length || template[start + 1] != '{')
        {
            return false;
        }

        var close = template.IndexOf('}', start + 2);
        if (close < 0)
        {
            return false;
        }

        var inner = template[(start + 2)..close].Trim();
        if (inner.Length == 0 || inner.Any(char.IsWhiteSpace))
        {
            return false;
        }

        name = inner;
        end = close;
        return true;
    }

    private static bool IsEscaped(string template, int index)
    {
        var backslashes = 0;
        for (var k = index - 1; k >= 0 && template[k] == '\\'; k--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    private static void AppendJsonEscaped(StringBuilder sb, string value)
    {
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Navigates a dotted path into the parsed response. Numeric segments index
    /// into arrays. Returns false when any segment cannot be resolved.
    /// </summary>
    public static bool TryExtract(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out var next))
                {
                    return false;
                }

                value = next;
            }
            else if (value.ValueKind == JsonValueKind.Array
                && int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (index < 0 || index >= value.GetArrayLength())
                {
                    return false;
                }

                value = value[index];
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static string ToScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText()
    };
}
