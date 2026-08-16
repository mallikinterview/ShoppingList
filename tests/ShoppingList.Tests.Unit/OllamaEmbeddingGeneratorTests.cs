using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Infrastructure.Embeddings;
using ShoppingList.Api.Telemetry;

namespace ShoppingList.Tests.Unit;

/// <summary>
/// The contract of <see cref="IEmbeddingGenerator"/> is that an unavailable embedder yields a
/// null vector, never an exception. Everything downstream depends on it: search falls back to
/// keyword-only, item creation still succeeds, and the item is embedded later by the sweep.
/// <para>
/// That contract was previously exercised only through the integration suite's stub generator,
/// which returns null by construction. So the test proved the search service handles null
/// correctly, and never asked whether the real client produces null. For two of the five ways it
/// can fail, it did not — a resilience pipeline wraps the client and substitutes its own
/// exception types when it gives up, and neither was caught. A stopped Ollama therefore returned
/// 504 from search instead of degrading, the exact opposite of the documented behaviour, while
/// every test passed.
/// </para>
/// <para>
/// Hence a test against the real class with the real exception types, rather than another
/// assertion about a stub written to satisfy it.
/// </para>
/// </summary>
public sealed class OllamaEmbeddingGeneratorTests
{
    // Named rather than passed as objects: xunit wants theory arguments it can serialise, and an
    // Exception is not one. The name maps to a real failure mode in Failure() below.
    [Theory]
    [InlineData("connection-refused")]
    [InlineData("client-timeout")]
    [InlineData("framework-timeout")]
    [InlineData("resilience-timeout")]
    [InlineData("circuit-open")]
    public async Task An_unavailable_embedder_returns_null_rather_than_throwing(string failureMode)
    {
        using var fixture = CreateGenerator(Failure(failureMode));

        var vector = await fixture.Client.GenerateAsync("dish soap", CancellationToken.None);

        vector.Should().BeNull(
            "an embedder failing with {0} must degrade search to keyword-only, not fail the request",
            failureMode);
    }

    [Fact]
    public async Task A_failed_generation_is_not_cached()
    {
        // A cached failure outlives its cause. Without eviction, one blip pins "no embedding"
        // against that query for the full six-hour entry lifetime, so search stays degraded long
        // after Ollama recovers — an outage the system inflicts on itself.
        using var fixture = CreateGenerator(Failure("connection-refused"));

        (await fixture.Client.GenerateAsync("dish soap", CancellationToken.None)).Should().BeNull();
        fixture.Handler.Attempts.Should().Be(1);

        // The second call must reach the transport again. If the empty result had been cached,
        // this would still read 1.
        (await fixture.Client.GenerateAsync("dish soap", CancellationToken.None)).Should().BeNull();
        fixture.Handler.Attempts.Should().Be(2, "a failed generation must not be cached");
    }

    private static Exception Failure(string mode) => mode switch
    {
        "connection-refused" => new HttpRequestException("Connection refused"),
        "client-timeout" => new TaskCanceledException("The request was canceled due to timeout"),
        "framework-timeout" => new TimeoutException("The operation timed out"),

        // Thrown by the resilience pipeline, not by the transport. Derives from
        // ExecutionRejectedException rather than System.TimeoutException, which is precisely why
        // it slipped past the original catch filter.
        "resilience-timeout" => new TimeoutRejectedException("The operation didn't complete within the allowed timeout"),

        // Thrown without any call being attempted at all: the breaker is open.
        "circuit-open" => new BrokenCircuitException("The circuit is now open and is not allowing calls"),

        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown failure mode.")
    };

    private static Fixture CreateGenerator(Exception failure)
    {
        var handler = new FailingHandler(failure);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        var provider = services.BuildServiceProvider();

        var settings = Options.Create(new OllamaSettings
        {
            BaseUrl = "http://localhost:11434",
            EmbeddingModel = "nomic-embed-text",
            EmbeddingDimensions = 768
        });

        var client = new OllamaEmbeddingGenerator(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") },
            provider.GetRequiredService<HybridCache>(),
            settings,
            new ApiMetrics(new TestMeterFactory()),
            NullLogger<OllamaEmbeddingGenerator>.Instance);

        return new Fixture(client, handler, provider);
    }

    private sealed class Fixture(
        OllamaEmbeddingGenerator client,
        FailingHandler handler,
        ServiceProvider provider) : IDisposable
    {
        public OllamaEmbeddingGenerator Client => client;

        public FailingHandler Handler => handler;

        public void Dispose() => provider.Dispose();
    }

    /// <summary>
    /// Fails every request with the supplied exception and counts attempts. No resilience
    /// pipeline is wired up here on purpose: what is under test is what this class does with the
    /// exception, not how many times Polly would have produced it.
    /// </summary>
    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromException<HttpResponseMessage>(failure);
        }
    }
}
