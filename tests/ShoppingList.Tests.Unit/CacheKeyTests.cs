using ShoppingList.Api.Infrastructure.Caching;

namespace ShoppingList.Tests.Unit;

/// <summary>
/// Every test here guards against a cache key that omits a dimension it should include. Two of
/// them describe outright bugs rather than inefficiencies: a missing user is a cross-account
/// data leak, and a missing variant silently invalidates the experiment.
/// </summary>
public sealed class CacheKeyTests
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly Guid ItemA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ItemB = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    [Fact]
    public void Different_users_never_share_a_key()
    {
        // If this fails, one user's search results are served to another. No amount of care in
        // the data layer prevents it, because the query never runs.
        var a = CacheKeys.SearchResults(UserA, "control", "milk", null, null, 20, 0);
        var b = CacheKeys.SearchResults(UserB, "control", "milk", null, null, 20, 0);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Different_variants_never_share_a_key()
    {
        // If this fails, a treatment user is served control results. The experiment then
        // measures nothing while continuing to report numbers that look entirely plausible —
        // the worst kind of failure, because nothing appears broken.
        var control = CacheKeys.SearchResults(UserA, "control", "milk", null, null, 20, 0);
        var treatment = CacheKeys.SearchResults(UserA, "treatment", "milk", null, null, 20, 0);

        control.Should().NotBe(treatment);
    }

    [Fact]
    public void Off_is_a_distinct_namespace_from_control()
    {
        var off = CacheKeys.SearchResults(UserA, "off", "milk", null, null, 20, 0);
        var control = CacheKeys.SearchResults(UserA, "control", "milk", null, null, 20, 0);

        off.Should().NotBe(control,
            "results computed before an experiment started must not be served into its control arm");
    }

    [Theory]
    [InlineData("milk", "bread")]
    [InlineData("milk", "MILK ")]
    public void Different_queries_produce_different_keys(string first, string second)
    {
        CacheKeys.SearchResults(UserA, "control", first, null, null, 20, 0)
            .Should().NotBe(CacheKeys.SearchResults(UserA, "control", second, null, null, 20, 0));
    }

    [Fact]
    public void Every_filter_dimension_changes_the_key()
    {
        var baseline = CacheKeys.SearchResults(UserA, "control", "milk", null, null, 20, 0);

        CacheKeys.SearchResults(UserA, "control", "milk", "dairy", null, 20, 0).Should().NotBe(baseline);
        CacheKeys.SearchResults(UserA, "control", "milk", null, true, 20, 0).Should().NotBe(baseline);
        CacheKeys.SearchResults(UserA, "control", "milk", null, null, 50, 0).Should().NotBe(baseline);
        CacheKeys.SearchResults(UserA, "control", "milk", null, null, 20, 20).Should().NotBe(baseline);
    }

    [Fact]
    public void Identical_inputs_produce_an_identical_key()
    {
        // The other half of correctness: a key that varies on something it should not — a
        // timestamp, a random value — would produce a cache with a permanent 0% hit rate that
        // still appears to be working.
        CacheKeys.SearchResults(UserA, "control", "milk", "dairy", true, 20, 0)
            .Should().Be(CacheKeys.SearchResults(UserA, "control", "milk", "dairy", true, 20, 0));
    }

    [Fact]
    public void Filter_values_cannot_collide_across_positions()
    {
        // Concatenating fields without a separator lets ("ab", null) and ("a", "b") produce the
        // same key — a genuine collision serving one filter's results for another's.
        CacheKeys.SearchResults(UserA, "control", "milk", "ab", null, 20, 0)
            .Should().NotBe(CacheKeys.SearchResults(UserA, "control", "milk", "a", null, 20, 0));
    }

    [Fact]
    public void Query_text_is_hashed_rather_than_embedded()
    {
        // Raw user input in a key allows separator injection and unbounded key length.
        var key = CacheKeys.SearchResults(UserA, "control", "chocolate digestive biscuits", null, null, 20, 0);

        key.Should().NotContain("chocolate");
        key.Length.Should().BeLessThan(120);
    }

    [Fact]
    public void User_version_key_is_scoped_per_user()
    {
        // Invalidation bumps this counter. Sharing it across users would mean one person's
        // write flushes everybody's cache.
        CacheKeys.UserVersion(UserA).Should().NotBe(CacheKeys.UserVersion(UserB));
    }

    [Fact]
    public void Version_participates_in_the_final_key()
    {
        var baseKey = CacheKeys.SearchResults(UserA, "control", "milk", null, null, 20, 0);

        CacheKeys.Versioned(1, baseKey)
            .Should().NotBe(CacheKeys.Versioned(2, baseKey),
                "bumping the version is what makes invalidation work without enumerating keys");
    }

    [Fact]
    public void Version_applies_to_item_keys_as_well_as_search_keys()
    {
        // One version stamp covers every cached shape for a user. If item reads were versioned
        // separately — or not at all — a write would flush the search results while leaving a
        // stale copy of the same row reachable through GET /items/{id}, and the two endpoints
        // would disagree about the data until the TTL expired.
        var itemKey = CacheKeys.Item(UserA, ItemA);

        CacheKeys.Versioned(1, itemKey)
            .Should().NotBe(CacheKeys.Versioned(2, itemKey));
    }

    [Fact]
    public void Item_key_is_scoped_per_user()
    {
        // The same item id under two callers must never collide. The key is derived from the
        // authenticated caller rather than from the row, so a lookup cannot cross accounts even
        // if the row itself were mislabelled.
        CacheKeys.Item(UserA, ItemA).Should().NotBe(CacheKeys.Item(UserB, ItemA));
    }

    [Fact]
    public void Item_key_distinguishes_items()
    {
        CacheKeys.Item(UserA, ItemA).Should().NotBe(CacheKeys.Item(UserA, ItemB));
    }

    [Fact]
    public void Item_list_key_is_scoped_per_user()
    {
        CacheKeys.ItemList(UserA, null, 20, null, null)
            .Should().NotBe(CacheKeys.ItemList(UserB, null, 20, null, null));
    }

    [Theory]
    // Every input that changes the page must change the key. A missing dimension serves one
    // query's answer to a different question — the same class of bug as omitting the user.
    [InlineData("cursor-abc", 20, null, null)]
    [InlineData(null, 5, null, null)]
    [InlineData(null, 20, "Dairy", null)]
    [InlineData(null, 20, null, true)]
    public void Item_list_key_changes_with_every_parameter(
        string? cursor, int pageSize, string? category, bool? isPurchased)
    {
        var baseline = CacheKeys.ItemList(UserA, null, 20, null, null);

        CacheKeys.ItemList(UserA, cursor, pageSize, category, isPurchased)
            .Should().NotBe(baseline);
    }

    [Fact]
    public void Item_list_key_is_stable_for_identical_input()
    {
        // Stability is what makes it a cache rather than a write-only store.
        CacheKeys.ItemList(UserA, "cursor-abc", 10, "Dairy", false)
            .Should().Be(CacheKeys.ItemList(UserA, "cursor-abc", 10, "Dairy", false));
    }
}
