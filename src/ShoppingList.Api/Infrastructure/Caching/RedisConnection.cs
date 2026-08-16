using StackExchange.Redis;

namespace ShoppingList.Api.Infrastructure.Caching;

/// <summary>
/// Holds the Redis connection, which may legitimately be absent.
/// <para>
/// A thin wrapper rather than registering <c>IConnectionMultiplexer?</c> directly: the container's
/// generic registration constrains services to <c>class</c>, which a nullable reference type does
/// not satisfy. More usefully, it makes "there may be no Redis" an explicit, named state that
/// every consumer must acknowledge, instead of a null that is easy to dereference by accident.
/// </para>
/// <para>
/// The absence is not an error condition. Redis is a cache: losing it costs latency, not
/// correctness, and the API serves from the database until it returns.
/// </para>
/// </summary>
internal sealed class RedisConnection(IConnectionMultiplexer? multiplexer)
{
    public IConnectionMultiplexer? Multiplexer { get; } = multiplexer;

    public bool IsAvailable => Multiplexer is { IsConnected: true };

    public IDatabase Database => Multiplexer!.GetDatabase();
}
