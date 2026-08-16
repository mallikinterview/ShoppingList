using System.Globalization;

namespace ShoppingList.Api.Common.Pagination;

/// <summary>
/// Keyset (cursor) pagination rather than offset.
/// <para>
/// <c>OFFSET n</c> makes Postgres read and discard n rows, so page 500 costs five hundred times
/// page 1, and any row inserted or deleted mid-traversal shifts the window — callers silently skip
/// or repeat items. A cursor anchored on the sort key is O(log n) at every depth and stable under
/// concurrent writes.
/// </para>
/// <para>
/// The cursor is opaque by design. Publishing a decodable structure invites clients to construct
/// their own, which turns an implementation detail into an API contract.
/// </para>
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    int PageSize)
{
    public bool HasMore => NextCursor is not null;
}

public static class Cursor
{
    /// <summary>Encodes a sort-key pair as a URL-safe opaque string.</summary>
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        var raw = $"{createdAt.UtcDateTime:O}|{id}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Decodes a cursor. Returns false rather than throwing on anything malformed — the caller
    /// decides what that means. The list endpoint treats it as a client error and answers 400;
    /// silently restarting from the first page instead would leave a paginating client looping
    /// over page 1 forever with no signal that anything was wrong.
    /// </summary>
    public static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = DateTimeOffset.MinValue;
        id = Guid.Empty;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var normalised = cursor.Replace('-', '+').Replace('_', '/');
            normalised = normalised.PadRight(normalised.Length + (4 - normalised.Length % 4) % 4, '=');

            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(normalised));
            var parts = raw.Split('|', 2);

            return parts.Length == 2
                   && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind, out createdAt)
                   && Guid.TryParse(parts[1], out id);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
