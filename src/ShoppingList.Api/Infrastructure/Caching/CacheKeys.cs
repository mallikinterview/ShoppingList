using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ShoppingList.Api.Infrastructure.Caching;

/// <summary>
/// Cache key construction, in one place so the rules cannot drift.
/// <para>
/// <b>Every input that changes the result must appear in the key.</b> This is not a style
/// preference — a missing dimension is a correctness bug, and in the two cases below it is a
/// security or data-integrity bug:
/// </para>
/// <list type="bullet">
/// <item><b>User.</b> Omit it and one user's search results are served to another. That is a
/// cross-account data leak that no amount of query-filter discipline in the data layer prevents,
/// because the query never runs.</item>
/// <item><b>Ranking variant.</b> Omit it and a treatment user is served results computed by the
/// control strategy. The experiment then measures nothing, while continuing to report numbers
/// that look entirely plausible. Silent invalidation is worse than an obvious failure.</item>
/// </list>
/// <para>
/// Query text is hashed rather than embedded: raw user input in a key would allow separators to
/// be injected, and unbounded key length is its own problem.
/// </para>
/// </summary>
internal static class CacheKeys
{
    private const string Prefix = "sl";

    public static string SearchResults(
        Guid userId,
        string variant,
        string query,
        string? category,
        bool? isPurchased,
        int limit,
        int offset)
    {
        var filters = string.Create(CultureInfo.InvariantCulture,
            $"{category ?? "-"}|{isPurchased?.ToString() ?? "-"}|{limit}|{offset}");

        return $"{Prefix}:search:{userId:N}:{variant}:{Hash(query)}:{Hash(filters)}";
    }

    /// <summary>
    /// Version stamp for a user's cached data. Bumping it invalidates every search result for
    /// that user at once.
    /// <para>
    /// This indirection exists because search keys are hashed over query text and filters, so
    /// they cannot be enumerated to delete individually. The alternatives are worse: <c>KEYS</c>
    /// is O(n) and blocks the Redis event loop, and tracking every key written per user costs a
    /// second write on every read. A version bump is one <c>INCR</c> and orphans the old keys,
    /// which then expire on their existing TTL.
    /// </para>
    /// </summary>
    public static string UserVersion(Guid userId) => $"{Prefix}:ver:{userId:N}";

    /// <summary>
    /// Stamps a key with the owning user's cache version. Applied to every key the cache writes,
    /// so a single version bump invalidates searches, item reads and list pages together — a
    /// write that changes an item must not leave a stale copy of it reachable through a
    /// different endpoint.
    /// </summary>
    public static string Versioned(long version, string baseKey) => $"{baseKey}:v{version}";

    /// <summary>
    /// A single item's response. Keyed by user as well as item id even though an item belongs to
    /// exactly one user: the key is what the cache trusts, and deriving it from the caller rather
    /// than from the row means a lookup can never cross accounts even if the row were mislabelled.
    /// </summary>
    public static string Item(Guid userId, Guid itemId) => $"{Prefix}:item:{userId:N}:{itemId:N}";

    /// <summary>
    /// One page of a user's item list. Every input that changes the page is in the key — cursor,
    /// size and both filters — for the same reason it is in the search key: a missing dimension
    /// serves one query's answer to a different question.
    /// </summary>
    public static string ItemList(
        Guid userId,
        string? cursor,
        int pageSize,
        string? category,
        bool? isPurchased)
    {
        var filters = string.Create(CultureInfo.InvariantCulture,
            $"{cursor ?? "-"}|{pageSize}|{category ?? "-"}|{isPurchased?.ToString() ?? "-"}");

        return $"{Prefix}:items:{userId:N}:{Hash(filters)}";
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
