using System.Globalization;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    /// <summary>
    /// Captures every caller-fixed value once for an inbox/access request. The
    /// repository adds row-scoped instance, workflow, node, and acting-for
    /// values before invoking the PostgreSQL evaluator.
    /// </summary>
    private InboxVisibilityEvaluationContext CreateInboxVisibilityContext(
        ActorContext actor,
        DateTimeOffset? capturedAt = null)
    {
        var asOf = capturedAt ?? timeProvider.GetUtcNow();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var normalizedUser = NormalizeUser(actor.User);
        if (!IsPostgresJsonSafe(normalizedUser))
        {
            // Authored identities cannot contain PostgreSQL's forbidden NUL or
            // invalid UTF-16. An empty lookup value cannot match a valid owner.
            normalizedUser = string.Empty;
        }
        var normalizedRoles = NormalizeRoles(actor.Roles)
            .Where(IsPostgresJsonSafe)
            .ToArray();

        static bool IsScalar(JsonElement value) => value.ValueKind is
            JsonValueKind.String or JsonValueKind.Number or
            JsonValueKind.True or JsonValueKind.False;

        void PutElement(string key, JsonElement value)
        {
            var canonicalKey = key.ToLowerInvariant();
            if (!IsPostgresJsonSafe(canonicalKey) || !IsScalar(value))
            {
                return;
            }

            if (value.ValueKind == JsonValueKind.String
                && !IsPostgresJsonSafe(value.GetString() ?? string.Empty))
            {
                return;
            }

            values[canonicalKey] = value.Clone();
        }

        void PutString(string key, string? value)
        {
            if (value is null || !IsPostgresJsonSafe(value))
            {
                return;
            }

            PutElement(key, JsonSerializer.SerializeToElement(value));
        }

        PutString("sys.now", asOf.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        PutString("sys.today", asOf.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        PutString("sys.user", normalizedUser);

        foreach (var allowed in contextOptions.AllowedClaims ?? [])
        {
            if (!string.IsNullOrWhiteSpace(allowed)
                && IsPostgresJsonSafe(allowed)
                && TryResolveClaim(actor.Claims, allowed, out var claimValue))
            {
                PutString($"sys.claim.{allowed}", claimValue);
            }
        }

        if (contextOptions.Config is { } config)
        {
            foreach (var pair in config)
            {
                if (IsPostgresJsonSafe(pair.Key))
                {
                    PutString($"config.{pair.Key}", pair.Value);
                }
            }
        }

        if (_settingsCache is { } cache)
        {
            foreach (var pair in cache)
            {
                PutElement(pair.Key, pair.Value);
            }
        }

        return new InboxVisibilityEvaluationContext(
            normalizedUser,
            normalizedRoles,
            asOf,
            values);
    }

    private Task<bool> IsUserTaskInboxVisibleAsync(
        long taskId,
        ActorContext actor,
        InboxVisibilityEvaluationContext visibilityContext,
        CancellationToken cancellationToken) =>
        runtime.IsUserTaskVisibleAsync(
            taskId,
            visibilityContext,
            actor.ActingFor,
            cancellationToken);

    private Task<bool> IsUserTaskNodeInboxVisibleAsync(
        long instanceId,
        int nodeId,
        ActorContext actor,
        InboxVisibilityEvaluationContext visibilityContext,
        CancellationToken cancellationToken) =>
        runtime.IsUserTaskNodeVisibleAsync(
            instanceId,
            nodeId,
            visibilityContext,
            actor.ActingFor,
            cancellationToken);

    private static bool IsPostgresJsonSafe(string value)
    {
        if (value.Contains('\0'))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(current))
            {
                return false;
            }
        }

        return true;
    }
}
