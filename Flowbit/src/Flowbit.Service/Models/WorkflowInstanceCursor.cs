using System.Buffers.Binary;

namespace Flowbit.Service.Models;

/// <summary>
/// Opaque, versioned keyset cursor for workflow-instance pages.
/// </summary>
public static class WorkflowInstanceCursor
{
    private const byte Version = 1;
    private const int FixedPayloadSize = 2 + (sizeof(long) * 3);
    private const int MaximumEncodedLength = 64;

    public static IReadOnlyList<InstanceSortCriterion> NormalizeSort(
        IReadOnlyList<InstanceSortCriterion> sort)
    {
        ArgumentNullException.ThrowIfNull(sort);

        IReadOnlyList<InstanceSortCriterion> requested = sort.Count == 0
            ?
            [
                new InstanceSortCriterion(
                    InstanceSortField.UpdatedAt,
                    SortDirection.Descending)
            ]
            : sort;

        if (requested.Count > 3)
        {
            throw new ArgumentException(
                "At most three instance sort criteria are supported.",
                nameof(sort));
        }

        var normalized = new List<InstanceSortCriterion>(requested.Count + 1);
        var fields = new HashSet<InstanceSortField>();
        foreach (var criterion in requested)
        {
            ValidateCriterion(criterion, sort);
            if (!fields.Add(criterion.Field))
            {
                throw new ArgumentException(
                    $"Instance sort field '{criterion.Field}' was specified more than once.",
                    nameof(sort));
            }

            normalized.Add(criterion);
        }

        if (!fields.Contains(InstanceSortField.Id))
        {
            normalized.Add(new InstanceSortCriterion(
                InstanceSortField.Id,
                normalized[^1].Direction));
        }

        return normalized.ToArray();
    }

    public static string Encode(
        IReadOnlyList<InstanceSortCriterion> normalizedSort,
        long id,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(id),
                id,
                "A workflow-instance cursor ID must be positive.");
        }

        var sort = NormalizeSort(normalizedSort);
        var payload = new byte[FixedPayloadSize + sort.Count];
        payload[0] = Version;
        payload[1] = checked((byte)sort.Count);
        for (var index = 0; index < sort.Count; index++)
        {
            payload[2 + index] = EncodeCriterion(sort[index]);
        }

        var valuesOffset = 2 + sort.Count;
        BinaryPrimitives.WriteInt64BigEndian(
            payload.AsSpan(valuesOffset, sizeof(long)),
            id);
        BinaryPrimitives.WriteInt64BigEndian(
            payload.AsSpan(valuesOffset + sizeof(long), sizeof(long)),
            createdAt.ToUniversalTime().Ticks);
        BinaryPrimitives.WriteInt64BigEndian(
            payload.AsSpan(valuesOffset + (sizeof(long) * 2), sizeof(long)),
            updatedAt.ToUniversalTime().Ticks);

        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(
        string? cursor,
        IReadOnlyList<InstanceSortCriterion> normalizedSort,
        out WorkflowInstanceCursorValues values)
    {
        values = default;
        var expectedSort = NormalizeSort(normalizedSort);
        if (!TryDecodeBase64Url(cursor, out var payload)
            || payload.Length < FixedPayloadSize + 1
            || payload[0] != Version)
        {
            return false;
        }

        var criterionCount = payload[1];
        if (criterionCount != expectedSort.Count
            || payload.Length != FixedPayloadSize + criterionCount)
        {
            return false;
        }

        for (var index = 0; index < criterionCount; index++)
        {
            if (payload[2 + index] != EncodeCriterion(expectedSort[index]))
            {
                return false;
            }
        }

        var valuesOffset = 2 + criterionCount;
        var id = BinaryPrimitives.ReadInt64BigEndian(
            payload.AsSpan(valuesOffset, sizeof(long)));
        var createdAtTicks = BinaryPrimitives.ReadInt64BigEndian(
            payload.AsSpan(valuesOffset + sizeof(long), sizeof(long)));
        var updatedAtTicks = BinaryPrimitives.ReadInt64BigEndian(
            payload.AsSpan(valuesOffset + (sizeof(long) * 2), sizeof(long)));
        if (id <= 0
            || !IsValidTimestamp(createdAtTicks)
            || !IsValidTimestamp(updatedAtTicks))
        {
            return false;
        }

        values = new WorkflowInstanceCursorValues(
            id,
            new DateTimeOffset(createdAtTicks, TimeSpan.Zero),
            new DateTimeOffset(updatedAtTicks, TimeSpan.Zero));
        return true;
    }

    private static void ValidateCriterion(
        InstanceSortCriterion criterion,
        IReadOnlyList<InstanceSortCriterion> sort)
    {
        if (criterion.Field is < InstanceSortField.Id or > InstanceSortField.UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sort),
                criterion.Field,
                "Unsupported instance sort field.");
        }

        if (criterion.Direction is < SortDirection.Ascending or > SortDirection.Descending)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sort),
                criterion.Direction,
                "Unsupported sort direction.");
        }
    }

    private static byte EncodeCriterion(InstanceSortCriterion criterion) =>
        (byte)(((int)criterion.Field * 2) + (int)criterion.Direction);

    private static bool IsValidTimestamp(long ticks) =>
        ticks >= DateTimeOffset.MinValue.Ticks
        && ticks <= DateTimeOffset.MaxValue.Ticks;

    private static bool TryDecodeBase64Url(string? cursor, out byte[] payload)
    {
        payload = [];
        if (string.IsNullOrWhiteSpace(cursor)
            || cursor.Length > MaximumEncodedLength
            || cursor.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            return false;
        }

        var base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => string.Empty
        };
        if (base64.Length == 0)
        {
            return false;
        }

        try
        {
            payload = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public readonly record struct WorkflowInstanceCursorValues(
    long Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
