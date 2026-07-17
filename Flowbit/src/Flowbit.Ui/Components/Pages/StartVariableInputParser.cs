using System.Globalization;
using System.Text.Json;
using Flowbit.Shared.Models;

namespace Flowbit.Ui.Components.Pages;

internal static class StartVariableInputParser
{
    public static bool TryParse(
        VariableModel variable,
        string raw,
        out JsonElement value,
        out string? error)
    {
        try
        {
            value = variable.IsArray
                ? ParseArray(variable, raw)
                : ParseScalar(variable.DataType, raw);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or OverflowException)
        {
            value = default;
            error = $"Variable '{variable.Name}' must be {variable.DataType}{(variable.IsArray ? "[]" : string.Empty)}.";
            return false;
        }
    }

    private static JsonElement ParseArray(VariableModel variable, string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException();
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                ValidateJsonScalar(item, variable.DataType);
            }

            return document.RootElement.Clone();
        }

        if (variable.DataType == WorkflowVariableTypes.Json)
        {
            throw new FormatException();
        }

        var items = raw.Split(',', StringSplitOptions.TrimEntries);
        if (items.Any(string.IsNullOrWhiteSpace))
        {
            throw new FormatException();
        }

        var parsed = items.Select(item => ParseScalar(variable.DataType, item)).ToArray();
        return JsonSerializer.SerializeToElement(parsed.Select(item =>
            JsonSerializer.Deserialize<object?>(item.GetRawText())).ToArray());
    }

    private static JsonElement ParseScalar(string dataType, string raw) => dataType switch
    {
        WorkflowVariableTypes.String => JsonSerializer.SerializeToElement(raw),
        WorkflowVariableTypes.Number => JsonSerializer.SerializeToElement(
            decimal.Parse(raw, NumberStyles.Number, CultureInfo.InvariantCulture)),
        WorkflowVariableTypes.Boolean => JsonSerializer.SerializeToElement(
            bool.Parse(raw)),
        WorkflowVariableTypes.Date when DateOnly.TryParseExact(
            raw,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _) => JsonSerializer.SerializeToElement(raw),
        WorkflowVariableTypes.DateTime when raw.Contains('T')
            && DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _) => JsonSerializer.SerializeToElement(raw),
        WorkflowVariableTypes.Json => ParseJson(raw),
        _ => throw new FormatException()
    };

    private static JsonElement ParseJson(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static void ValidateJsonScalar(JsonElement value, string dataType)
    {
        var valid = dataType switch
        {
            WorkflowVariableTypes.String => value.ValueKind == JsonValueKind.String,
            WorkflowVariableTypes.Number => value.ValueKind == JsonValueKind.Number,
            WorkflowVariableTypes.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            WorkflowVariableTypes.Date => value.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(
                    value.GetString(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _),
            WorkflowVariableTypes.DateTime => value.ValueKind == JsonValueKind.String
                && value.GetString()?.Contains('T') == true
                && value.TryGetDateTimeOffset(out _),
            WorkflowVariableTypes.Json => value.ValueKind != JsonValueKind.Undefined,
            _ => false
        };

        if (!valid)
        {
            throw new FormatException();
        }
    }
}
