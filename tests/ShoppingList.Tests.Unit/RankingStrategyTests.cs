using ShoppingList.Api.Features.Search;

namespace ShoppingList.Tests.Unit;

/// <summary>
/// The fusion arithmetic is the part of this system most likely to be subtly wrong and least
/// likely to look wrong. A ranking that is inverted, or dominated by one branch because scores
/// were never normalised, still returns results in a confident-looking order.
/// </summary>
public sealed class RankingStrategyTests
{
    [Fact]
    public void Rrf_score_decreases_as_rank_worsens()
    {
        // 1/(k+rank) is monotonically decreasing. If this ever inverted, search would return
        // its worst matches first while every other test still passed.
        const int k = 60;

        var first = RrfScore(k, 1);
        var second = RrfScore(k, 2);
        var tenth = RrfScore(k, 10);

        first.Should().BeGreaterThan(second);
        second.Should().BeGreaterThan(tenth);
    }

    [Fact]
    public void Rrf_rewards_agreement_between_branches()
    {
        // The core reason to fuse at all: an item both branches rank highly should outrank one
        // that only a single branch found, even if that single branch ranked it first.
        const int k = 60;

        var foundByBoth = RrfScore(k, 3) + RrfScore(k, 3);
        var foundByOneAtTop = RrfScore(k, 1);

        foundByBoth.Should().BeGreaterThan(foundByOneAtTop);
    }

    [Fact]
    public void Larger_k_flattens_the_difference_between_ranks()
    {
        // k controls how sharply the head of the list is favoured. A test on this documents
        // what the constant actually does, so changing it is a decision rather than a guess.
        var sharp = RrfScore(1, 1) - RrfScore(1, 5);
        var flat = RrfScore(200, 1) - RrfScore(200, 5);

        flat.Should().BeLessThan(sharp);
    }

    [Fact]
    public void Rrf_sql_coalesces_missing_ranks_to_zero()
    {
        // A FULL OUTER JOIN leaves NULL on whichever side did not find the row. Without
        // COALESCE, NULL propagates through the addition and the whole fused score becomes
        // NULL — so every result found by only one branch silently disappears.
        var sql = new RrfRankingStrategy(60).FusionExpression;

        sql.Should().Contain("COALESCE");
        sql.Should().Contain("60");
    }

    [Fact]
    public void Weighted_strategy_normalises_both_branches()
    {
        // Cosine similarity and ts_rank_cd are on different scales. Combining them without
        // min-max normalisation lets whichever has the larger magnitude dominate, producing a
        // number that looks like a blended score and is really just one signal in disguise.
        var sql = new WeightedRankingStrategy(0.6, 0.4).FusionExpression;

        sql.Should().Contain("min_value");
        sql.Should().Contain("max_value");
    }

    [Fact]
    public void Weighted_strategy_scores_a_single_candidate_as_a_full_match()
    {
        // Regression test for a defect the integration suite caught: a branch returning exactly
        // one candidate has max = min and therefore no range to normalise against. Guarding only
        // the division — NULLIF(max - min, 0) wrapped in COALESCE(..., 0) — scores that candidate
        // 0.0, indistinguishable from a row the branch never retrieved. A search matching a single
        // item then reports zero relevance for a perfect hit, and reports it confidently.
        var sql = new WeightedRankingStrategy(0.6, 0.4).FusionExpression;

        sql.Should().Contain("max_value = vs.min_value THEN 1.0",
            "a tied-best vector candidate normalises to 1, not 0");
        sql.Should().Contain("max_value = ts.min_value THEN 1.0",
            "a tied-best text candidate normalises to 1, not 0");
        sql.Should().NotContain("NULLIF",
            "the degenerate range is a ranking decision, not a division to be silenced");
    }

    [Fact]
    public void Weighted_strategy_treats_an_unmatched_branch_as_zero()
    {
        // Distinct from the tie case above and easy to conflate with it: NULL means the branch
        // never retrieved this row, which must contribute nothing rather than propagate NULL
        // through the sum and erase the score the other branch did earn.
        var sql = new WeightedRankingStrategy(0.6, 0.4).FusionExpression;

        sql.Should().Contain("v.similarity IS NULL THEN 0.0");
        sql.Should().Contain("t.score IS NULL THEN 0.0");
    }

    [Fact]
    public void Weighted_strategy_declares_the_statistics_it_needs()
    {
        var strategy = new WeightedRankingStrategy(0.6, 0.4);

        strategy.StatisticsCommonTableExpressions.Should().Contain("vector_stats");
        strategy.StatisticsCommonTableExpressions.Should().Contain("text_stats");
        strategy.StatisticsJoins.Should().Contain("CROSS JOIN");
    }

    [Fact]
    public void Rrf_needs_no_statistics()
    {
        // Fusing on rank rather than score is what removes the need for normalisation, and is
        // why RRF is the default.
        var strategy = new RrfRankingStrategy(60);

        strategy.StatisticsCommonTableExpressions.Should().BeEmpty();
        strategy.StatisticsJoins.Should().BeEmpty();
    }

    [Theory]
    [InlineData("rrf")]
    [InlineData("weighted")]
    public void Generated_sql_orders_vector_distance_ascending(string strategyName)
    {
        // <=> returns DISTANCE, not similarity. Sorting it DESC returns the least relevant
        // results first — an inversion that produces a full page of plausible-looking rows and
        // would pass any test that only checks "results were returned".
        IRankingStrategy strategy = strategyName == "rrf"
            ? new RrfRankingStrategy(60)
            : new WeightedRankingStrategy(0.6, 0.4);

        var sql = HybridSearchSql.Build(strategy);

        sql.Should().Contain("ORDER BY i.embedding <=> @queryEmbedding",
            "the vector branch must sort by ascending distance to use the HNSW index correctly");
        sql.Should().NotContain("<=> @queryEmbedding DESC");
    }

    [Theory]
    [InlineData("rrf")]
    [InlineData("weighted")]
    public void Generated_sql_applies_metadata_filters_to_both_branches(string strategyName)
    {
        // Filtering only one branch means the two are fusing over different populations, and
        // filtering only after fusion wastes each branch's candidate budget on rows that are
        // about to be discarded.
        IRankingStrategy strategy = strategyName == "rrf"
            ? new RrfRankingStrategy(60)
            : new WeightedRankingStrategy(0.6, 0.4);

        var sql = HybridSearchSql.Build(strategy);

        CountOccurrences(sql, "@category IS NULL OR i.category = @category").Should().Be(2);
        CountOccurrences(sql, "@isPurchased IS NULL OR i.is_purchased = @isPurchased").Should().Be(2);
        CountOccurrences(sql, "i.user_id = @userId").Should().Be(2,
            "ownership must be enforced in both retrieval branches, not applied afterwards");
    }

    [Theory]
    [InlineData("rrf")]
    [InlineData("weighted")]
    public void Vector_branch_applies_a_relevance_floor(string strategyName)
    {
        // Without a distance threshold an ANN index returns its k nearest rows whether or not
        // any of them are actually near, so on a small corpus every query matches everything.
        // The fusion is still arithmetically correct — it is just ranking a meaningless
        // candidate set, which looks like working search right up until someone notices that
        // "dish soap" returns bread.
        IRankingStrategy strategy = strategyName == "rrf"
            ? new RrfRankingStrategy(60)
            : new WeightedRankingStrategy(0.6, 0.4);

        var sql = HybridSearchSql.Build(strategy);

        sql.Should().Contain("(i.embedding <=> @queryEmbedding) < @maxVectorDistance");
    }

    [Fact]
    public void Relevance_floor_applies_only_to_the_vector_branch()
    {
        // The text branch has its own relevance gate: search_vector @@ tsq already excludes
        // non-matching rows. Applying a distance threshold there would be meaningless, and
        // applying it after fusion would filter results the text branch legitimately found.
        var sql = HybridSearchSql.Build(new RrfRankingStrategy(60));

        CountOccurrences(sql, "@maxVectorDistance").Should().Be(1);
    }

    [Fact]
    public void Generated_sql_uses_full_outer_join()
    {
        // An INNER JOIN would discard every semantic-only match — precisely the results that
        // justify having a vector branch — and every keyword match on an item not yet embedded.
        HybridSearchSql.Build(new RrfRankingStrategy(60))
            .Should().Contain("FULL OUTER JOIN");
    }

    [Fact]
    public void Generated_sql_uses_websearch_to_tsquery()
    {
        // to_tsquery raises a syntax error on malformed input, so a user typing "milk &" would
        // produce a 500. websearch_to_tsquery accepts what people actually type and never throws.
        var sql = HybridSearchSql.Build(new RrfRankingStrategy(60));

        sql.Should().Contain("websearch_to_tsquery");
        sql.Should().NotContain("plainto_tsquery");
    }

    [Fact]
    public void Generated_sql_binds_every_request_value_as_a_parameter()
    {
        // The only interpolation into this SQL comes from the strategy — constants and
        // configured weights. Nothing from the request is concatenated.
        var sql = HybridSearchSql.Build(new WeightedRankingStrategy(0.6, 0.4));

        foreach (var parameter in new[] { "@userId", "@query", "@queryEmbedding", "@category", "@isPurchased", "@candidateLimit", "@limit", "@offset" })
        {
            sql.Should().Contain(parameter);
        }
    }

    [Fact]
    public void Generated_sql_breaks_score_ties_deterministically()
    {
        // Without a tiebreaker, equally scored rows are returned in whatever order the executor
        // happens to produce, so pagination can repeat or skip items and ranking assertions
        // become flaky.
        HybridSearchSql.Build(new RrfRankingStrategy(60))
            .Should().Contain("ORDER BY f.fused_score DESC, i.created_at DESC, i.id DESC");
    }

    private static double RrfScore(int k, int rank) => 1.0 / (k + rank);

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
