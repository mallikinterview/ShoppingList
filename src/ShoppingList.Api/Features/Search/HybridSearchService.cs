using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Experimentation;
using ShoppingList.Api.Infrastructure.Caching;
using ShoppingList.Api.Infrastructure.Embeddings;
using ShoppingList.Api.Telemetry;

namespace ShoppingList.Api.Features.Search;

public interface IHybridSearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, Guid userId, CancellationToken ct);
}

internal sealed class HybridSearchService(
    NpgsqlDataSource dataSource,
    IEmbeddingGenerator embeddingGenerator,
    IVariantAssigner variantAssigner,
    IItemCache cache,
    IOptions<SearchSettings> options,
    ApiMetrics metrics,
    ILogger<HybridSearchService> logger) : IHybridSearchService
{
    private readonly SearchSettings _settings = options.Value;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, Guid userId, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        var assignment = variantAssigner.Assign(userId);
        var strategy = ResolveStrategy(assignment.Strategy);

        // The variant is part of the cache key. This is the correctness constraint that makes
        // the experiment valid: without it, whichever variant computed a result first would
        // serve it to users in the other arm, and the comparison would silently measure nothing
        // while continuing to produce plausible-looking numbers.
        var cacheKey = CacheKeys.SearchResults(
            userId, assignment.Variant, request.Query,
            request.Category, request.IsPurchased, request.Limit, request.Offset);

        var cacheHit = true;

        var results = await cache.GetOrCreateAsync(
            cacheKey,
            userId,
            async token =>
            {
                cacheHit = false;
                return await ExecuteAsync(request, userId, strategy, token);
            },
            ct) ?? new CachedSearchResult([], false);

        stopwatch.Stop();

        metrics.RecordCache(cacheHit, assignment.Variant);
        metrics.RecordSearch(stopwatch.Elapsed.TotalSeconds, results.Hits.Count, assignment.Variant, strategy.Name);

        // Variant and strategy go into the log scope, so a Loki query can isolate one arm's
        // traffic and read exactly what those users experienced.
        logger.LogInformation(
            "Search '{Query}' returned {Count} result(s) using {Strategy} (variant {Variant}, vector {VectorUsed}, cached {Cached}) in {Duration:F1}ms",
            request.Query, results.Hits.Count, strategy.Name, assignment.Variant,
            results.VectorSearchUsed, cacheHit, stopwatch.Elapsed.TotalMilliseconds);

        return new SearchResponse(
            results.Hits,
            results.Hits.Count,
            new SearchDiagnostics(
                assignment.Variant,
                strategy.Name,
                results.VectorSearchUsed,
                cacheHit,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2)));
    }

    private IRankingStrategy ResolveStrategy(string name) => name switch
    {
        "weighted" => new WeightedRankingStrategy(_settings.VectorWeight, _settings.TextWeight),
        _ => new RrfRankingStrategy(_settings.RrfK)
    };

    private async Task<CachedSearchResult> ExecuteAsync(
        SearchRequest request,
        Guid userId,
        IRankingStrategy strategy,
        CancellationToken ct)
    {
        // Null when Ollama is unavailable, or when the item corpus has not been embedded yet.
        // Search continues on the text branch alone rather than failing — a degraded result is
        // enormously better than an error, and the caller is told which happened via the
        // vectorSearchUsed diagnostic rather than being left to guess.
        var queryEmbedding = await embeddingGenerator.GenerateAsync(request.Query, ct);

        if (queryEmbedding is null)
        {
            logger.LogWarning("No query embedding available; search is running keyword-only.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await TryEnableIterativeScanAsync(connection, ct);

        var sql = HybridSearchSql.Build(strategy);

        await using var command = new NpgsqlCommand(sql, connection);

        // Every value is a parameter. The only interpolated fragments in this SQL come from the
        // ranking strategy — constants this application controls — and never from the request.
        command.Parameters.Add(new NpgsqlParameter("userId", NpgsqlDbType.Uuid) { Value = userId });
        command.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = request.Query });
        command.Parameters.Add(new NpgsqlParameter("queryEmbedding", queryEmbedding is null
            ? DBNull.Value
            : queryEmbedding));
        command.Parameters.Add(new NpgsqlParameter("category", NpgsqlDbType.Text)
        {
            Value = (object?)request.Category ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("isPurchased", NpgsqlDbType.Boolean)
        {
            Value = (object?)request.IsPurchased ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("candidateLimit", NpgsqlDbType.Integer)
        {
            Value = _settings.CandidateLimit
        });
        command.Parameters.Add(new NpgsqlParameter("maxVectorDistance", NpgsqlDbType.Double)
        {
            Value = _settings.MaxVectorDistance
        });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = request.Limit });
        command.Parameters.Add(new NpgsqlParameter("offset", NpgsqlDbType.Integer) { Value = request.Offset });

        var hits = new List<SearchHit>(request.Limit);

        await using var reader = await command.ExecuteReaderAsync(ct);

        // Async accessors throughout. With a synchronous IsDBNull or GetFieldValue the thread
        // blocks whenever the next row has not yet arrived from the network, which under load
        // starves the thread pool — the classic way an async pipeline is undone at the last step.
        while (await reader.ReadAsync(ct))
        {
            hits.Add(new SearchHit(
                Id: await reader.GetFieldValueAsync<Guid>(0, ct),
                Name: await reader.GetFieldValueAsync<string>(1, ct),
                Notes: await ReadNullableAsync<string>(reader, 2, ct),
                Quantity: await reader.GetFieldValueAsync<int>(3, ct),
                Unit: await ReadNullableAsync<string>(reader, 4, ct),
                Category: await ReadNullableAsync<string>(reader, 5, ct),
                IsPurchased: await reader.GetFieldValueAsync<bool>(6, ct),
                CreatedAt: await reader.GetFieldValueAsync<DateTimeOffset>(7, ct),
                Score: await reader.GetFieldValueAsync<double>(8, ct),
                VectorSimilarity: await ReadNullableStructAsync<double>(reader, 9, ct),
                TextScore: await ReadNullableStructAsync<double>(reader, 10, ct),
                VectorRank: (int?)await ReadNullableStructAsync<long>(reader, 11, ct),
                TextRank: (int?)await ReadNullableStructAsync<long>(reader, 12, ct)));
        }

        return new CachedSearchResult(hits, queryEmbedding is not null);
    }

    /// <summary>
    /// Enables pgvector's iterative index scan for this connection.
    /// <para>
    /// This addresses the sharpest edge in filtered vector search. An HNSW scan returns its
    /// nearest neighbours from the whole index, and the <c>WHERE user_id = ...</c> filter is
    /// applied afterwards — so if none of the global top-k happen to belong to this user, the
    /// branch returns nothing at all despite the user having perfectly good matches. Iterative
    /// scan keeps pulling from the index until enough rows survive the filter.
    /// </para>
    /// <para>
    /// Applied as a best-effort setting: it requires pgvector 0.8 or later, and on an older
    /// build the query is still correct — recall on heavily filtered searches is simply lower.
    /// Failing the search over a missing optimisation would be the wrong trade.
    /// </para>
    /// </summary>
    private async Task TryEnableIterativeScanAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SET LOCAL hnsw.iterative_scan = 'relaxed_order'; SET LOCAL hnsw.ef_search = 100;",
                connection);

            await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex)
        {
            logger.LogDebug(ex,
                "pgvector iterative scan is unavailable (requires 0.8+). Filtered vector recall may be reduced.");
        }
    }

    private static async Task<T?> ReadNullableAsync<T>(NpgsqlDataReader reader, int ordinal, CancellationToken ct)
        where T : class =>
        await reader.IsDBNullAsync(ordinal, ct) ? null : await reader.GetFieldValueAsync<T>(ordinal, ct);

    private static async Task<T?> ReadNullableStructAsync<T>(NpgsqlDataReader reader, int ordinal, CancellationToken ct)
        where T : struct =>
        await reader.IsDBNullAsync(ordinal, ct) ? null : await reader.GetFieldValueAsync<T>(ordinal, ct);

    internal sealed record CachedSearchResult(IReadOnlyList<SearchHit> Hits, bool VectorSearchUsed);
}
