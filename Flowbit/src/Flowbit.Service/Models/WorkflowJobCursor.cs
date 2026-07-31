using System.Globalization;
using System.Text;

namespace Flowbit.Service.Models;

/// <summary>
/// Opaque, versioned keyset cursors for durable operations pages. Cursor
/// contents are intentionally an implementation detail of the API.
/// </summary>
public static class WorkflowJobCursor
{
    private const string Version = "v1";

    public static string EncodeJob(DateTimeOffset updatedAt, long id) =>
        Encode("job", updatedAt.ToUniversalTime().Ticks, id);

    public static string EncodeIncident(DateTimeOffset updatedAt, long id) =>
        Encode("incident", updatedAt.ToUniversalTime().Ticks, id);

    public static string EncodeAttempt(int attemptNumber, long id) =>
        Encode("attempt", attemptNumber, id);

    public static bool TryDecodeJob(
        string? cursor,
        out DateTimeOffset updatedAt,
        out long id) =>
        TryDecodeTimestamp("job", cursor, out updatedAt, out id);

    public static bool TryDecodeIncident(
        string? cursor,
        out DateTimeOffset updatedAt,
        out long id) =>
        TryDecodeTimestamp("incident", cursor, out updatedAt, out id);

    public static bool TryDecodeAttempt(
        string? cursor,
        out int attemptNumber,
        out long id)
    {
        attemptNumber = 0;
        id = 0;
        if (!TryDecode("attempt", cursor, out var first, out id)
            || first is <= 0 or > int.MaxValue)
        {
            return false;
        }
        attemptNumber = (int)first;
        return true;
    }

    private static bool TryDecodeTimestamp(
        string kind,
        string? cursor,
        out DateTimeOffset updatedAt,
        out long id)
    {
        updatedAt = default;
        id = 0;
        if (!TryDecode(kind, cursor, out var ticks, out id)
            || ticks < DateTimeOffset.MinValue.Ticks
            || ticks > DateTimeOffset.MaxValue.Ticks)
        {
            return false;
        }
        updatedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    private static string Encode(string kind, long first, long id)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}|{kind}|{first}|{id}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecode(
        string expectedKind,
        string? cursor,
        out long first,
        out long id)
    {
        first = 0;
        id = 0;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 256)
        {
            return false;
        }

        try
        {
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

            var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var parts = text.Split('|');
            return parts.Length == 4
                   && parts[0] == Version
                   && parts[1] == expectedKind
                   && long.TryParse(
                       parts[2],
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out first)
                   && long.TryParse(
                       parts[3],
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out id)
                   && id > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
