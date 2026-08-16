using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Telemetry;

namespace ShoppingList.Api.Common.Extensions;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var serilogSettings = configuration.GetSettings<SerilogSettings>(SerilogSettings.SectionName);

        services.AddSingleton<ApiMetrics>();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: serilogSettings.ApplicationName,
                    serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString())
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", environment.EnvironmentName)]))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(ApiMetrics.MeterName)
                // Explicit bucket boundaries. The SDK default histogram buckets top out well
                // above anything this API should take, which makes p95 and p99 read as flat
                // lines at a bucket edge rather than as real latency.
                .AddView(
                    instrumentName: "http.server.request.duration",
                    metricStreamConfiguration: new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10]
                    })
                .AddView(
                    instrumentName: "shoppinglist.search.duration",
                    metricStreamConfiguration: new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5]
                    })
                .AddView(
                    instrumentName: "shoppinglist.search.results",
                    metricStreamConfiguration: new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0, 1, 2, 5, 10, 20, 50]
                    })
                .AddPrometheusExporter());

        return services;
    }

    /// <summary>
    /// Two health endpoints with different meanings, because conflating them causes outages.
    /// <para>
    /// <c>/health/live</c> answers "is this process functioning" and checks nothing external. If
    /// a liveness probe included dependencies, a brief Redis blip would cause the orchestrator to
    /// kill and restart every replica — turning a degraded cache into a full outage.
    /// </para>
    /// <para>
    /// <c>/health/ready</c> answers "can this instance serve traffic" and does check dependencies.
    /// Redis is tagged separately so it can be reported without being treated as fatal: the API
    /// serves from the database when the cache is unavailable.
    /// </para>
    /// </summary>
    public static IServiceCollection AddApplicationHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var database = configuration.GetSettings<DatabaseSettings>(DatabaseSettings.SectionName);
        var redis = configuration.GetSettings<RedisSettings>(RedisSettings.SectionName);
        var ollama = configuration.GetSettings<OllamaSettings>(OllamaSettings.SectionName);
        var keycloak = configuration.GetSettings<KeycloakSettings>(KeycloakSettings.SectionName);

        // Every check is bounded. Without an explicit timeout each one inherits its client's
        // default — 100 seconds for HttpClient — so a dependency that hangs rather than refusing
        // makes the readiness endpoint hang with it. The orchestrator's probe then times out and
        // kills a process that is otherwise serving traffic, which converts a slow dependency
        // into an outage. A refused connection fails fast and hides this; a black-holed one does
        // not, and that is the failure that actually happens in production.
        var probeTimeout = TimeSpan.FromSeconds(3);

        services.AddHealthChecks()
            .AddNpgSql(
                connectionString: database.ConnectionString,
                name: "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "critical"],
                timeout: probeTimeout)
            .AddRedis(
                redisConnectionString: redis.ConnectionString,
                name: "redis",
                // Degraded, not Unhealthy: losing the cache costs latency, not correctness.
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "cache"],
                timeout: probeTimeout)
            .AddUrlGroup(
                new Uri($"{ollama.BaseUrl.TrimEnd('/')}/api/tags"),
                name: "ollama",
                // Also degraded: search falls back to keyword-only ranking without embeddings.
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "embeddings"],
                timeout: probeTimeout)
            .AddUrlGroup(
                new Uri($"{keycloak.MetadataAddress}"),
                name: "keycloak",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "critical"],
                timeout: probeTimeout);

        return services;
    }
}
