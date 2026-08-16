using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Pgvector;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Telemetry;

namespace ShoppingList.Api.Infrastructure.Embeddings;

internal sealed class OllamaEmbeddingGenerator(
    HttpClient httpClient,
    HybridCache cache,
    IOptions<OllamaSettings> options,
    ApiMetrics metrics,
    ILogger<OllamaEmbeddingGenerator> logger) : IEmbeddingGenerator
{
    private readonly OllamaSettings _settings = options.Value;

    public string ModelName => _settings.EmbeddingModel;

    public int Dimensions => _settings.EmbeddingDimensions;

    public async Task<Vector?> GenerateAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalised = Normalise(text);

        // Cached on the normalised text plus the model name. Search queries repeat constantly
        // — the same handful of terms, over and over — and each cache hit removes a full model
        // inference from the request path. The model name is part of the key because a model
        // change must invalidate every cached vector; without it, upgrading the model would
        // silently serve embeddings from the previous one.
        //
        // The cached type is float[], not Vector. HybridCache serialises every value it stores
        // — including into its in-process layer, because it will not hand out shared references
        // to mutable objects — and Pgvector.Vector has no parameterless constructor, so
        // System.Text.Json can write it but cannot read it back. That fails on retrieval rather
        // than on storage, which is a particularly unhelpful place to discover it. A float[] is
        // the natural transport shape anyway: it is exactly what the model returns.
        var key = $"embed:{_settings.EmbeddingModel}:{normalised.GetHashCode(StringComparison.Ordinal)}:{normalised.Length}";

        var values = await cache.GetOrCreateAsync(
            key,
            normalised,
            async (input, token) => await CallOllamaAsync(input, token) ?? [],
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromHours(6),
                LocalCacheExpiration = TimeSpan.FromMinutes(10)
            },
            cancellationToken: ct);

        if (values.Length == 0)
        {
            // A failed generation must not be cached. Without this, one Ollama blip would poison
            // the key for six hours and keep returning "no embedding" long after the service
            // recovered — a self-inflicted outage that outlives its cause.
            await cache.RemoveAsync(key, ct);
            return null;
        }

        return new Vector(values);
    }

    private async Task<float[]?> CallOllamaAsync(string text, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "/api/embeddings",
                new OllamaEmbeddingRequest(_settings.EmbeddingModel, text),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Ollama returned {StatusCode} generating an embedding", (int)response.StatusCode);
                metrics.EmbeddingFailures.Add(1);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: ct);

            if (payload?.Embedding is null || payload.Embedding.Length == 0)
            {
                logger.LogWarning("Ollama returned an empty embedding");
                metrics.EmbeddingFailures.Add(1);
                return null;
            }

            // A dimension mismatch would be rejected by Postgres with an opaque error from
            // inside a background worker. Caught here, it names the configuration that is wrong.
            if (payload.Embedding.Length != _settings.EmbeddingDimensions)
            {
                logger.LogError(
                    "Model '{Model}' returned {Actual} dimensions but OllamaSettings__EmbeddingDimensions is {Expected}. " +
                    "The pgvector column is fixed-width, so this requires a schema migration and a re-embed.",
                    _settings.EmbeddingModel, payload.Embedding.Length, _settings.EmbeddingDimensions);

                metrics.EmbeddingFailures.Add(1);
                return null;
            }

            return payload.Embedding;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                         or TaskCanceledException
                                         or TimeoutException
                                         or TimeoutRejectedException
                                         or BrokenCircuitException)
        {
            // Swallowed on purpose, and this is the single most important decision in this class.
            // Ollama being down must not fail a write or a search: the item is still created and
            // is still findable by keyword, it simply lacks vector recall until the embedder
            // recovers. Rethrowing here would make the embedding service a hard dependency of
            // the whole API. The failure is visible in the metric and in the item's
            // embedding_status — not silent, just not fatal.
            //
            // The last two types are what turned that decision from a fact into a claim. A
            // resilience pipeline sits in front of this client, and when it gives up it does not
            // surface the transport error — it throws its own. Polly's TimeoutRejectedException
            // derives from ExecutionRejectedException, not System.TimeoutException, and
            // BrokenCircuitException is thrown without any call being attempted at all. Neither
            // matched the original filter, so both escaped this method, propagated out through
            // the search endpoint and became a 504. Stopping Ollama turned every uncached query
            // into a gateway timeout while this comment asserted the opposite.
            //
            // It generalises: whatever wraps a client can substitute the exception that client's
            // own error handling was written against.
            logger.LogWarning(ex, "Ollama unavailable; embedding skipped. Search degrades to keyword-only.");
            metrics.EmbeddingFailures.Add(1);
            return null;
        }
        finally
        {
            stopwatch.Stop();
            metrics.EmbeddingDuration.Record(stopwatch.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Applied identically at index time and query time. If the two ever diverge — one trims,
    /// the other does not — every query vector lands in a slightly different region of the
    /// embedding space than the documents it should match, and relevance degrades in a way that
    /// looks like a bad model rather than a bug.
    /// </summary>
    internal static string Normalise(string text) =>
        string.Join(' ', text.Trim().ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed record OllamaEmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt);

    private sealed record OllamaEmbeddingResponse(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
