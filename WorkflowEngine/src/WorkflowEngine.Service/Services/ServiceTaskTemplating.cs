using System.Globalization;
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
/// - In a JSON body a placeholder is replaced by the variable's JSON
///   representation (<see cref="JsonElement.GetRawText"/>), so authors write
///   <c>${name}</c> unquoted and still get valid JSON; a missing variable
///   becomes <c>null</c>.
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

        return PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            return variables.TryGetValue(name, out var value) ? value.GetRawText() : "null";
        });
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
