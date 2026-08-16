namespace ShoppingList.Api.Infrastructure.Caching;

public interface IItemCache
{
    /// <summary>
    /// Cache-aside with single-flight protection: concurrent misses on the same key produce one
    /// call to <paramref name="factory"/>, not N.
    /// </summary>
    Task<T?> GetOrCreateAsync<T>(
        string key,
        Guid userId,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct) where T : class;

    /// <summary>Invalidates everything cached for a user by bumping their version stamp.</summary>
    Task InvalidateUserAsync(Guid userId, CancellationToken ct);
}
