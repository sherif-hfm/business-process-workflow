using System.Text.Json;

namespace Flowbit.Service.Services;

internal static class SettingManagementValidation
{
    private const int MaximumIdentifierLength = 300;
    private const int MaximumDescriptionLength = 1000;

    public static string? NormalizeNamespace(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > MaximumIdentifierLength)
        {
            throw new WorkflowDomainException(
                $"namespace must not exceed {MaximumIdentifierLength} characters.");
        }

        return normalized;
    }

    public static string NormalizeIdentifier(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            throw new WorkflowDomainException($"{fieldName} is required.");
        }

        if (normalized.Length > MaximumIdentifierLength)
        {
            throw new WorkflowDomainException(
                $"{fieldName} must not exceed {MaximumIdentifierLength} characters.");
        }

        return normalized;
    }

    public static string NormalizeEngineValue(string? value)
    {
        if (value is null)
        {
            throw new WorkflowDomainException("value is required.");
        }

        // Engine-setting values are opaque strings. Preserve intentional leading,
        // trailing, multiline, and empty content exactly as authored.
        return value;
    }

    public static string? NormalizeDescription(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > MaximumDescriptionLength)
        {
            throw new WorkflowDomainException(
                $"description must not exceed {MaximumDescriptionLength} characters.");
        }

        return normalized;
    }

    public static JsonElement NormalizeWorkflowValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new WorkflowDomainException("value must be valid JSON.");
        }

        return value.Clone();
    }

    public static void ValidateMutation(long id, DateTimeOffset expectedUpdatedAt)
    {
        if (id <= 0)
        {
            throw new WorkflowDomainException("id must be positive.");
        }

        if (expectedUpdatedAt == default)
        {
            throw new WorkflowDomainException("expectedUpdatedAt is required.");
        }
    }
}
