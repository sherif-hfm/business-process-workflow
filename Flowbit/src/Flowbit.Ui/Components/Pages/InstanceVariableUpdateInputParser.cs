using System.Text.Json;

namespace Flowbit.Ui.Components.Pages;

internal static class InstanceVariableUpdateInputParser
{
    public const int MaxVariableCount = 100;
    public const int MaxNameLength = 300;

    private static readonly string[] ReservedPrefixes = ["sys.", "config.", "setting.", "mi."];

    public static bool TryParseValue(
        string raw,
        out JsonElement value,
        out string kind,
        out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            value = document.RootElement.Clone();
            kind = KindLabel(value.ValueKind);
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            value = default;
            kind = string.Empty;
            error = $"Enter one valid JSON value: {exception.Message}";
            return false;
        }
    }

    public static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "Variable name is required.";
        }

        if (trimmed.EnumerateRunes().Count() > MaxNameLength)
        {
            return $"Variable name cannot exceed {MaxNameLength} Unicode characters.";
        }

        if (ReservedPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return "Variable names cannot start with sys., config., setting., or mi.";
        }

        return null;
    }

    public static IReadOnlyDictionary<int, string> FindDuplicateNameErrors(
        IReadOnlyList<string?> names)
    {
        var errors = new Dictionary<int, string>();
        var groups = names
            .Select((name, index) => new { Name = name?.Trim(), Index = index })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);

        foreach (var group in groups)
        {
            foreach (var item in group)
            {
                errors[item.Index] = $"Variable name '{group.Key}' is duplicated (names are case-insensitive).";
            }
        }

        return errors;
    }

    private static string KindLabel(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "JSON"
    };
}
