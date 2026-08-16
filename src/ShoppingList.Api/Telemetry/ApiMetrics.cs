using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ShoppingList.Api.Telemetry;

/// <summary>
/// Application metrics, registered once as a singleton.
/// <para>
/// Instruments are created here and nowhere else. Creating a counter per request is a common and
/// expensive mistake: each instance becomes its own time series, and the resulting cardinality
/// explosion degrades Prometheus long before anyone notices the dashboards are wrong.
/// </para>
/// <para>
/// Tag values are bounded by construction — variant, strategy, cache result, dependency name.
/// User ids and query text never appear as tags; they belong in logs, where they are queryable
/// without multiplying series.
/// </para>
/// <para>
/// Names here are the contract with <c>docker/prometheus/prometheus.yml</c> and the committed
/// Grafana dashboard. Renaming an instrument without updating the dashboard produces empty panels
/// that look identical to broken instrumentation.
/// </para>
/// </summary>
public sealed class ApiMetrics : IDisposable
{
    public const string MeterName = "ShoppingList.Api";

    private readonly Meter _meter;
    private int _embeddingQueueDepth;

    public ApiMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        SearchDuration = _meter.CreateHistogram<double>(
            "shoppinglist.search.duration",
            unit: "s",
            description: "Hybrid search latency, tagged by ranking variant and strategy.");

        SearchResults = _meter.CreateHistogram<int>(
            "shoppinglist.search.results",
            unit: "{item}",
            description: "Number of results returned per search, tagged by ranking variant.");

        SearchZeroResults = _meter.CreateCounter<long>(
            "shoppinglist.search.zero_results",
            unit: "{search}",
            description: "Searches returning no results. Paired with search.duration count, this gives the zero-result rate — the clearest signal that a ranking change made relevance worse.");

        CacheRequests = _meter.CreateCounter<long>(
            "shoppinglist.cache.requests",
            unit: "{request}",
            description: "Cache lookups tagged hit or miss, and by ranking variant so per-variant cache isolation is observable.");

        EmbeddingDuration = _meter.CreateHistogram<double>(
            "shoppinglist.embedding.duration",
            unit: "s",
            description: "Time spent generating an embedding. Off the request path — this is the background worker's latency.");

        EmbeddingFailures = _meter.CreateCounter<long>(
            "shoppinglist.embedding.failures",
            unit: "{failure}",
            description: "Failed embedding generations. Non-zero here alongside a healthy API is the graceful-degradation path working as designed.");

        RateLimitRejections = _meter.CreateCounter<long>(
            "shoppinglist.ratelimit.rejections",
            unit: "{request}",
            description: "Requests rejected with 429, tagged by policy.");

        ExperimentAssignments = _meter.CreateCounter<long>(
            "shoppinglist.experiment.assignments",
            unit: "{assignment}",
            description: "Variant assignments. The observed distribution should converge on the configured split; a persistent skew means the assignment hash is not uniform.");

        _meter.CreateObservableGauge(
            "shoppinglist.embedding.queue_depth",
            () => Volatile.Read(ref _embeddingQueueDepth),
            unit: "{item}",
            description: "Backlog on the background embedding channel. Should drain to zero shortly after writes stop.");
    }

    public Histogram<double> SearchDuration { get; }
    public Histogram<int> SearchResults { get; }
    public Counter<long> SearchZeroResults { get; }
    public Counter<long> CacheRequests { get; }
    public Histogram<double> EmbeddingDuration { get; }
    public Counter<long> EmbeddingFailures { get; }
    public Counter<long> RateLimitRejections { get; }
    public Counter<long> ExperimentAssignments { get; }

    public void RecordSearch(double seconds, int resultCount, string variant, string strategy)
    {
        var tags = new TagList
        {
            { "variant", variant },
            { "strategy", strategy }
        };

        SearchDuration.Record(seconds, tags);
        SearchResults.Record(resultCount, new TagList { { "variant", variant } });

        if (resultCount == 0)
        {
            SearchZeroResults.Add(1, new TagList { { "variant", variant } });
        }
    }

    public void RecordCache(bool hit, string variant) =>
        CacheRequests.Add(1, new TagList
        {
            { "result", hit ? "hit" : "miss" },
            { "variant", variant }
        });

    public void RecordAssignment(string variant) =>
        ExperimentAssignments.Add(1, new TagList { { "variant", variant } });

    public void RecordRateLimitRejection(string policy) =>
        RateLimitRejections.Add(1, new TagList { { "policy", policy } });

    public void EmbeddingQueued() => Interlocked.Increment(ref _embeddingQueueDepth);

    public void EmbeddingDequeued() => Interlocked.Decrement(ref _embeddingQueueDepth);

    public void Dispose() => _meter.Dispose();
}
