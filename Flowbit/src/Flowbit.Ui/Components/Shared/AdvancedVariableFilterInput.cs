using System.Text.Json;

namespace Flowbit.Ui.Components.Shared;

public static class AdvancedVariableFilterInput
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true
    };

    public static bool TryParse(
        string? input,
        out JsonElement? variableFilter,
        out string? error)
    {
        variableFilter = null;
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The advanced variable filter must be a JSON object.";
                return false;
            }

            variableFilter = document.RootElement.Clone();
            return true;
        }
        catch (JsonException exception)
        {
            error = $"The advanced variable filter is not valid JSON: {exception.Message}";
            return false;
        }
    }

    public static bool TryFormat(
        string? input,
        out string formatted,
        out string? error)
    {
        formatted = input ?? string.Empty;
        if (!TryParse(input, out var variableFilter, out error))
        {
            return false;
        }

        if (variableFilter is null)
        {
            formatted = string.Empty;
            return true;
        }

        formatted = JsonSerializer.Serialize(variableFilter.Value, IndentedJson);
        return true;
    }
}
