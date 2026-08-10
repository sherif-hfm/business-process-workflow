using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

internal static class InstanceVariableUpdateValidation
{
    private static readonly string[] ReservedPrefixes =
        ["sys.", "config.", "setting.", "mi."];

    public static IReadOnlyList<InstanceVariableWriteDto> NormalizeWrites(
        IReadOnlyList<InstanceVariableWriteDto>? writes)
    {
        if (writes is null || writes.Count == 0)
        {
            throw new WorkflowDomainException(
                "At least one instance variable is required.");
        }
        if (writes.Count > InstanceVariableUpdateConstraints.MaxVariables)
        {
            throw new WorkflowDomainException(
                $"At most {InstanceVariableUpdateConstraints.MaxVariables} variables are allowed per update.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<InstanceVariableWriteDto>(writes.Count);
        foreach (var write in writes)
        {
            if (write is null)
            {
                throw new WorkflowDomainException(
                    "Variable update entries cannot be null.");
            }
            var name = write.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                throw new WorkflowDomainException("Variable name is required.");
            }
            if (name.EnumerateRunes().Count()
                > InstanceVariableUpdateConstraints.MaxVariableNameLength)
            {
                throw new WorkflowDomainException(
                    $"Variable '{name}' must contain at most {InstanceVariableUpdateConstraints.MaxVariableNameLength} Unicode characters.");
            }
            if (ReservedPrefixes.Any(prefix =>
                    name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new WorkflowDomainException(
                    $"Variable '{name}' uses a reserved context prefix.");
            }
            if (!names.Add(name))
            {
                throw new WorkflowDomainException(
                    $"Variable name '{name}' is duplicated; variable names are case-insensitive.");
            }
            if (write.Value.ValueKind == JsonValueKind.Undefined)
            {
                throw new WorkflowDomainException(
                    $"Variable '{name}' must contain a JSON value.");
            }
            normalized.Add(new InstanceVariableWriteDto(name, write.Value.Clone()));
        }
        return normalized;
    }

    public static string? NormalizeReason(string? value, string label = "Reason")
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }
        if (normalized.EnumerateRunes().Count()
            > InstanceVariableUpdateConstraints.MaxReasonLength)
        {
            throw new WorkflowDomainException(
                $"{label} cannot exceed {InstanceVariableUpdateConstraints.MaxReasonLength} Unicode characters.");
        }
        return normalized;
    }

    public static string? NormalizeIdempotencyKey(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }
        if (normalized.EnumerateRunes().Count()
            > InstanceVariableUpdateConstraints.MaxIdempotencyKeyLength)
        {
            throw new WorkflowDomainException(
                $"IdempotencyKey cannot exceed {InstanceVariableUpdateConstraints.MaxIdempotencyKeyLength} Unicode characters.");
        }
        return normalized;
    }

    public static string NormalizeWorkflowKey(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new WorkflowDomainException("WorkflowKey is required.");
        }
        if (normalized.EnumerateRunes().Count()
            > InstanceVariableUpdateConstraints.MaxWorkflowKeyLength)
        {
            throw new WorkflowDomainException(
                $"WorkflowKey cannot exceed {InstanceVariableUpdateConstraints.MaxWorkflowKeyLength} Unicode characters.");
        }
        return normalized;
    }

    public static JsonElement SerializeWrites(
        IReadOnlyList<InstanceVariableWriteDto> writes) =>
        JsonSerializer.SerializeToElement(writes);

    public static bool WritesEqual(
        JsonElement stored,
        IReadOnlyList<InstanceVariableWriteDto> writes) =>
        JsonElement.DeepEquals(stored, SerializeWrites(writes));

    public static string RequireActor(ActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var user = actor.User?.Trim();
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new WorkflowUnauthorizedException(
                "An authenticated workflow administrator is required.");
        }
        if (user.EnumerateRunes().Count()
            > InstanceVariableUpdateConstraints.MaxActorNameLength)
        {
            throw new WorkflowDomainException(
                "The workflow administrator name is too long.");
        }
        return user;
    }

    public static IReadOnlyList<string> SnapshotRoles(
        IReadOnlyCollection<string> roles) =>
        roles.Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
