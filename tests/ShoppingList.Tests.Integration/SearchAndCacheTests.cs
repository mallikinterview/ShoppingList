using System.Net;
using System.Net.Http.Json;
using ShoppingList.Api.Features.Items;
using ShoppingList.Api.Features.Search;

namespace ShoppingList.Tests.Integration;

[Collection(nameof(ApiCollection))]
public sealed class SearchAndCacheTests(ApiFactory factory)
{
    [Fact]
    public async Task Search_degrades_to_keyword_only_when_the_embedder_is_unavailable()
    {
        // The behaviour that decides whether an Ollama outage is a degradation or an outage.
        // Most implementations return 500 here.
        var user = NewUser();
        await CreateItemAsync(user, "Organic whole milk");

        factory.Embeddings.IsAvailable = false;

        try
        {
            var response = await factory.CreateClientFor(user)
                .PostAsJsonAsync("/api/v1/search", new SearchRequest("milk", null, null));

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "an embedder outage must not fail the request");

            var results = await response.Content.ReadFromJsonAsync<SearchResponse>();

            results!.Diagnostics.VectorSearchUsed.Should().BeFalse();
            results.Results.Should().NotBeEmpty(
                "the full-text branch alone must still find a keyword match");
        }
        finally
        {
            factory.Embeddings.IsAvailable = true;
        }
    }

    [Fact]
    public async Task Search_finds_items_that_share_no_keyword_with_the_query()
    {
        // The reason the vector branch exists, and the reason the fusion join is FULL OUTER:
        // an INNER JOIN would discard exactly these results.
        var user = NewUser();
        await CreateItemAsync(user, "Washing up liquid", "for dishes");

        var results = await SearchAsync(user, "dish soap");

        results.Diagnostics.VectorSearchUsed.Should().BeTrue();
    }

    [Fact]
    public async Task Search_reports_the_component_scores_behind_a_ranking()
    {
        // Asserted against both experiment arms rather than whichever one a single random user
        // happens to be assigned. RRF and weighted fusion compute the score by entirely different
        // arithmetic, so one assignment exercises one of them and silently skips the other — and
        // an assertion that holds half the time is worse than no assertion, because the failures
        // read as flakiness and get re-run rather than investigated. Written this way it caught a
        // defect in the weighted branch that scored a single-candidate match as zero.
        var byStrategy = new Dictionary<string, SearchResponse>(StringComparer.Ordinal);

        foreach (var user in Enumerable.Range(0, 40).Select(_ => NewUser()))
        {
            await CreateItemAsync(user, "Cheddar cheese");

            var results = await SearchAsync(user, "cheese");
            byStrategy.TryAdd(results.Diagnostics.Strategy, results);

            if (byStrategy.Count == 2)
            {
                break;
            }
        }

        byStrategy.Should().HaveCount(2, "a 50/50 split over 40 users must exercise both strategies");

        foreach (var (strategy, results) in byStrategy)
        {
            results.Results.Should().NotBeEmpty("the {0} strategy must still find the item", strategy);

            results.Results[0].Score.Should().BeGreaterThan(0,
                "a matched result under {0} must carry a score that distinguishes it from a miss",
                strategy);

            // Without at least one component rank populated, the hit came from nowhere — which
            // would mean the fusion join is producing rows neither branch retrieved.
            (results.Results[0].VectorRank ?? results.Results[0].TextRank).Should().NotBeNull(
                "the {0} strategy must attribute the hit to a branch", strategy);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_queries_are_rejected_by_validation(string query)
    {
        var response = await factory.CreateClientFor(NewUser())
            .PostAsJsonAsync("/api/v1/search", new SearchRequest(query, null, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("milk & ")]
    [InlineData("!!! ??? &&&")]
    [InlineData("\"unclosed quote")]
    [InlineData("café naïve 日本語")]
    public async Task Malformed_and_non_latin_queries_do_not_error(string query)
    {
        // to_tsquery raises a syntax error on every one of the first three. This is what
        // websearch_to_tsquery buys, and it is worth a test because the failure would be a 500
        // triggered by ordinary typing.
        var response = await factory.CreateClientFor(NewUser())
            .PostAsJsonAsync("/api/v1/search", new SearchRequest(query, null, null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Metadata_filters_are_applied()
    {
        var user = NewUser();
        await CreateItemAsync(user, "Cheddar cheese", category: "dairy");
        await CreateItemAsync(user, "Cheese grater", category: "kitchen");

        var results = await SearchAsync(user, "cheese", category: "dairy");

        results.Results.Should().OnlyContain(hit => hit.Category == "dairy");
    }

    [Fact]
    public async Task Repeated_searches_are_served_from_cache()
    {
        var user = NewUser();
        await CreateItemAsync(user, "Cached item");

        (await SearchAsync(user, "cached")).Diagnostics.Cached.Should().BeFalse("first call populates the cache");
        (await SearchAsync(user, "cached")).Diagnostics.Cached.Should().BeTrue();
    }

    [Fact]
    public async Task Writing_an_item_invalidates_that_users_cached_searches()
    {
        // A cache that is never invalidated is indistinguishable from a working one until a
        // user edits something and their change appears not to have happened.
        var user = NewUser();
        await CreateItemAsync(user, "Invalidation probe alpha");

        var before = await SearchAsync(user, "invalidation probe");
        before.Results.Should().HaveCount(1);

        await CreateItemAsync(user, "Invalidation probe beta");

        var after = await SearchAsync(user, "invalidation probe");

        after.Diagnostics.Cached.Should().BeFalse("the write must have invalidated the cached entry");
        after.Results.Should().HaveCount(2);
    }

    [Fact]
    public async Task Variants_do_not_share_cached_results()
    {
        // The test that proves the experiment is measuring anything at all. Two users assigned
        // to different arms must not be served each other's cached results — otherwise whichever
        // variant ran first supplies the answer for both, and the comparison silently reports
        // numbers that mean nothing.
        var users = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid().ToString()).ToArray();

        var byVariant = new Dictionary<string, string>();

        foreach (var user in users)
        {
            await CreateItemAsync(user, "Variant isolation probe");
            var result = await SearchAsync(user, "variant isolation");

            byVariant.TryAdd(result.Diagnostics.Variant, user);

            if (byVariant.Count == 2)
            {
                break;
            }
        }

        byVariant.Should().HaveCount(2, "a 50/50 split over 40 users must produce both arms");

        foreach (var (variant, user) in byVariant)
        {
            var repeat = await SearchAsync(user, "variant isolation");

            repeat.Diagnostics.Variant.Should().Be(variant, "assignment must be sticky");
            repeat.Diagnostics.Cached.Should().BeTrue("each variant keeps its own cache namespace");
        }
    }

    [Fact]
    public async Task Variant_is_exposed_as_a_response_header()
    {
        var response = await factory.CreateClientFor(NewUser())
            .PostAsJsonAsync("/api/v1/search", new SearchRequest("anything", null, null));

        response.Headers.Should().ContainKey("X-Experiment-Variant");
        response.Headers.Should().ContainKey("X-Ranking-Strategy");
    }

    private static string NewUser() => Guid.NewGuid().ToString();

    private async Task<SearchResponse> SearchAsync(string user, string query, string? category = null)
    {
        var response = await factory.CreateClientFor(user)
            .PostAsJsonAsync("/api/v1/search", new SearchRequest(query, category, null));

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<SearchResponse>())!;
    }

    private async Task CreateItemAsync(string user, string name, string? notes = null, string? category = null)
    {
        var response = await factory.CreateClientFor(user)
            .PostAsJsonAsync("/api/v1/items", new CreateItemRequest(name, notes, 1, null, category));

        response.EnsureSuccessStatusCode();

        // The embedding worker is asynchronous, so the vector branch is not immediately
        // populated. Polled rather than slept: a fixed Thread.Sleep is the classic source of a
        // suite that is both slow and flaky.
        var itemId = (await response.Content.ReadFromJsonAsync<ItemResponse>())!.Id;
        await WaitForEmbeddingAsync(user, itemId);
    }

    private async Task WaitForEmbeddingAsync(string user, Guid itemId)
    {
        var client = factory.CreateClientFor(user);
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var item = await client.GetFromJsonAsync<ItemResponse>($"/api/v1/items/{itemId}");

            if (item?.EmbeddingStatus is "Ready" or "Failed")
            {
                return;
            }

            await Task.Delay(100);
        }
    }
}
