using Microsoft.Extensions.Http.Resilience;
using Polly;
using ShoppingList.Api.Configuration;

namespace ShoppingList.Api.Infrastructure.Embeddings;

public static class EmbeddingExtensions
{
    public static IServiceCollection AddEmbeddings(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSettings<OllamaSettings>(OllamaSettings.SectionName);

        services.AddHttpClient<IEmbeddingGenerator, OllamaEmbeddingGenerator>(client =>
            {
                client.BaseAddress = new Uri(settings.BaseUrl);
                // Explicit, because HttpClient defaults to 100 seconds. A model server that
                // stops responding would otherwise hold a request — and a thread-pool thread —
                // for over a minute, which is how one slow dependency becomes a full outage.
                client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            })
            .AddResilienceHandler("ollama", builder =>
            {
                // Per-attempt timeout, shorter than the client timeout so retries fit inside it.
                builder.AddTimeout(TimeSpan.FromSeconds(Math.Max(settings.TimeoutSeconds / 2, 5)));

                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = settings.MaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    // Jitter matters here: without it, a wave of items queued together retries
                    // in lockstep and hammers a recovering service back down.
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(500)
                });

                // Fails fast once the model server is clearly down, instead of every queued item
                // waiting out its full retry budget. The queue drains quickly into Failed rows,
                // and the periodic sweep retries once the breaker closes.
                builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(20)
                });
            });

        services.AddSingleton<EmbeddingQueue>();
        services.AddSingleton<IEmbeddingQueue>(provider => provider.GetRequiredService<EmbeddingQueue>());
        services.AddHostedService<EmbeddingBackgroundService>();

        return services;
    }
}
