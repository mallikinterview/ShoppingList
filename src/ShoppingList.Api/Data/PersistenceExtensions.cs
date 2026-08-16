using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Data.Configurations;
using ShoppingList.Api.Data.Interceptors;

namespace ShoppingList.Api.Data;

public static class PersistenceExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSettings<DatabaseSettings>(DatabaseSettings.SectionName);

        services.TryAddSingletonTimeProvider();
        services.AddSingleton<AuditingInterceptor>();

        // A shared NpgsqlDataSource rather than a bare connection string. The data source owns
        // the connection pool and — critically here — the pgvector type mapping, which has to be
        // registered once on the source rather than per connection.
        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(BuildConnectionString(settings));
            builder.UseVector();
            return builder.Build();
        });

        services.AddDbContext<AppDbContext>((provider, options) =>
        {
            var dataSource = provider.GetRequiredService<NpgsqlDataSource>();

            options.UseNpgsql(dataSource, npgsql =>
            {
                npgsql.UseVector();
                npgsql.CommandTimeout(settings.CommandTimeoutSeconds);

                // Transient faults — a failover, a brief network partition — are retried by the
                // provider. Without this, a one-second blip surfaces to the caller as a 500.
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);

                npgsql.MigrationsHistoryTable("__ef_migrations_history");
            });

            options.AddInterceptors(provider.GetRequiredService<AuditingInterceptor>());

            // Query tracking is off by default. Read paths vastly outnumber writes here, and
            // tracking every returned entity means the change tracker holds the whole result
            // set alive for the request. The two write paths opt back in explicitly with
            // AsTracking(), which is a far safer default than the reverse.
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        return services;
    }

    private static string BuildConnectionString(DatabaseSettings settings)
    {
        var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString)
        {
            MaxPoolSize = settings.MaxPoolSize,
            CommandTimeout = settings.CommandTimeoutSeconds,
            // Bounded so a leaked connection surfaces as a clear timeout rather than as a
            // request that hangs until the client gives up.
            Timeout = 15,
            KeepAlive = 30,
            ApplicationName = "shopping-list-api"
        };

        return builder.ConnectionString;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }

    /// <summary>
    /// Applies migrations and exits. Invoked only by the <c>migrator</c> compose service, which
    /// the API waits on.
    /// <para>
    /// Deliberately not called from the API's startup path. With more than one replica, every
    /// instance would race to apply the same migration on deploy; Postgres serialises the DDL,
    /// so the losers fail their startup and crash-loop while the winner succeeds. A one-shot job
    /// makes migration a discrete, observable step that either succeeded or did not.
    /// </para>
    /// </summary>
    public static async Task RunDatabaseMigrationsAsync(this IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Migrator");
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToArray();

            if (pending.Length == 0)
            {
                logger.LogInformation("Database is up to date; no migrations to apply.");
            }
            else
            {
                logger.LogInformation("Applying {Count} migration(s): {Migrations}",
                    pending.Length, string.Join(", ", pending));

                await dbContext.Database.MigrateAsync();

                logger.LogInformation("Migrations applied successfully.");
            }

            AssertEmbeddingDimension(scope.ServiceProvider, logger);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database migration failed. The API will not start.");

            // Non-zero exit so the compose dependency on service_completed_successfully fails
            // fast and visibly, rather than the API starting against a half-migrated schema.
            Environment.ExitCode = 1;
            throw;
        }
    }

    /// <summary>
    /// Verifies the configured embedding dimension matches the pgvector column width.
    /// <para>
    /// A mismatch is otherwise invisible until the first embedding is written, at which point
    /// Postgres reports "expected 768 dimensions, not 1024" from inside a background worker —
    /// far from the configuration that caused it. Checking at migration time turns a confusing
    /// runtime failure into a startup error naming the exact setting.
    /// </para>
    /// </summary>
    private static void AssertEmbeddingDimension(IServiceProvider services, ILogger logger)
    {
        // Read as a single configuration value rather than through IOptions<OllamaSettings>.
        // That class requires a BaseUrl, and the migrator has no reason to know where the
        // embedding service lives — only what width the column has to be. Binding the whole
        // settings object here would force the migration job to be configured with a
        // dependency it never calls.
        var configured = services.GetRequiredService<IConfiguration>()
            .GetValue<int?>($"{OllamaSettings.SectionName}:{nameof(OllamaSettings.EmbeddingDimensions)}");

        if (configured is null)
        {
            logger.LogWarning(
                "OllamaSettings__EmbeddingDimensions is not configured; skipping the schema dimension check.");
            return;
        }

        if (configured == ShoppingItemConfiguration.EmbeddingDimensions)
        {
            logger.LogInformation(
                "Embedding dimension check passed: schema and configuration both use {Dimensions}.",
                configured);
            return;
        }

        var message =
            $"Embedding dimension mismatch: OllamaSettings__EmbeddingDimensions is " +
            $"{configured} but the shopping_items.embedding column is " +
            $"vector({ShoppingItemConfiguration.EmbeddingDimensions}). pgvector columns are " +
            $"fixed-width, so changing the model requires a schema migration and a re-embed of " +
            $"the existing corpus.";

        logger.LogCritical("{Message}", message);
        throw new InvalidOperationException(message);
    }
}
