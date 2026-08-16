using ShoppingList.Api.Configuration;
using StackExchange.Redis;

namespace ShoppingList.Api.Infrastructure.Caching;

public static class CachingExtensions
{
    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSettings<RedisSettings>(RedisSettings.SectionName);

        // Registered as a nullable singleton, connected once at startup. If the connection
        // cannot be established the application still starts with a null multiplexer, and every
        // cache call falls through to the source. AbortOnConnectFail=false is what makes this
        // possible: with the default of true, the constructor throws and the API cannot boot
        // without Redis — turning an optional cache into a mandatory dependency.
        // Wrapped rather than registered as IConnectionMultiplexer? directly: the container's
        // generic constraint is `class`, which a nullable reference type does not satisfy. The
        // holder makes "there may be no connection" an explicit, injectable state rather than a
        // null the compiler cannot reason about.
        services.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Redis");

            try
            {
                var config = ConfigurationOptions.Parse(settings.ConnectionString);
                config.AbortOnConnectFail = settings.AbortOnConnectFail;
                config.Ssl = settings.UseSsl;
                config.ConnectTimeout = 5000;
                config.SyncTimeout = 3000;
                config.ConnectRetry = 3;
                config.ClientName = "shopping-list-api";

                var multiplexer = ConnectionMultiplexer.Connect(config);

                multiplexer.ConnectionFailed += (_, args) =>
                    logger.LogWarning("Redis connection failed ({FailureType}); serving from source until it recovers.",
                        args.FailureType);

                multiplexer.ConnectionRestored += (_, _) =>
                    logger.LogInformation("Redis connection restored.");

                return new RedisConnection(multiplexer);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not connect to Redis at startup. The API will run without caching.");
                return new RedisConnection(null);
            }
        });

        services.AddSingleton<IItemCache, RedisItemCache>();

        // HybridCache backs embedding caching specifically: an L1 in-process layer in front of
        // L2 Redis. Query embeddings are small, immutable for a given model, and requested
        // repeatedly, so the in-process hit removes even the Redis round trip.
        services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = new Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(30),
                LocalCacheExpiration = TimeSpan.FromMinutes(5)
            };
        });

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = settings.ConnectionString;
            options.InstanceName = $"{settings.InstanceName}:";
        });

        return services;
    }
}
