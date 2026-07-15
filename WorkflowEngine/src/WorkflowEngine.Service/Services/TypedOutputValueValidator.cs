using System.Globalization;
using System.Text.Json;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Services;

internal static class TypedOutputValueValidator
{
    public static bool IsValid(JsonElement value, string dataType, bool isArray)
    {
        if (isArray)
        {
            return value.ValueKind == JsonValueKind.Array
                && value.EnumerateArray().All(item => IsScalarValid(item, dataType));
        }

        return IsScalarValid(value, dataType);
    }

    public static bool IsValidAuthoredDefault(JsonElement value, string dataType, bool isArray)
    {
        if (isArray)
        {
            return value.ValueKind == JsonValueKind.Array
                && value.EnumerateArray().All(item =>
                    IsScalarValid(item, dataType)
                    || IsTemplatedString(item));
        }

        if (IsValid(value, dataType, false) || IsTemplatedString(value))
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString();
        return dataType switch
        {
            WorkflowVariableTypes.Number =>
                double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _),
            WorkflowVariableTypes.Boolean => bool.TryParse(text, out _),
            _ => false
        };
    }

    public static string DescribeExpected(string dataType, bool isArray) =>
        dataType + (isArray ? "[]" : string.Empty);

    private static bool IsScalarValid(JsonElement value, string dataType) => dataType switch
    {
        WorkflowVariableTypes.String => value.ValueKind == JsonValueKind.String,
        WorkflowVariableTypes.Number => value.ValueKind == JsonValueKind.Number,
        WorkflowVariableTypes.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        WorkflowVariableTypes.Date => IsIsoDate(value),
        WorkflowVariableTypes.DateTime => IsIsoDateTime(value),
        WorkflowVariableTypes.Json => value.ValueKind is not JsonValueKind.Undefined,
        _ => false
    };

    private static bool IsTemplatedString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString()?.Contains("${", StringComparison.Ordinal) == true;

    private static bool IsIsoDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(
            value.GetString(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static bool IsIsoDateTime(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString();
        return text is not null
            && text.Contains('T')
            && value.TryGetDateTimeOffset(out _);
    }
}
