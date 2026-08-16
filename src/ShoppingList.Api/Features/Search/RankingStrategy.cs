namespace ShoppingList.Api.Features.Search;

/// <summary>
/// Supplies the fusion arithmetic for the hybrid query.
/// <para>
/// This is the one place in the codebase where a strategy abstraction genuinely earns itself:
/// there are two real implementations, they are selected at runtime from configuration, they are
/// compared against each other on live traffic, and the fusion maths is unit-testable in
/// isolation. That is the opposite of an interface introduced because a pattern suggested one.
/// </para>
/// <para>
/// Only the retrieval and fusion arithmetic differ between strategies. Both use the same
/// candidate CTEs, the same indexes and the same filters, so a measured difference between
/// variants is attributable to ranking rather than to two divergent query plans.
/// </para>
/// </summary>
public interface IRankingStrategy
{
    string Name { get; }

    /// <summary>Extra CTEs this strategy needs, or an empty string.</summary>
    string StatisticsCommonTableExpressions { get; }

    /// <summary>Joins bringing those CTEs into scope for the fusion expression.</summary>
    string StatisticsJoins { get; }

    /// <summary>The SQL expression producing the fused score.</summary>
    string FusionExpression { get; }
}

/// <summary>
/// Reciprocal Rank Fusion: <c>score = Σ 1 / (k + rank)</c> over each retrieval branch.
/// <para>
/// The default, because it solves the problem that makes naive hybrid search wrong. Cosine
/// distance and <c>ts_rank_cd</c> are not on the same scale, have no shared unit, and are not
/// even distributed the same way — cosine similarity clusters tightly in a narrow band while
/// text rank is unbounded and sparse. Adding or averaging them directly produces a number that
/// looks like a relevance score and is not one, and whichever signal happens to have the larger
/// magnitude quietly dominates the ranking.
/// </para>
/// <para>
/// RRF sidesteps this entirely by discarding the scores and fusing on <b>rank position</b>, which
/// is comparable across any two retrieval systems by construction. The <c>k</c> constant damps
/// the contribution of top positions so a single branch cannot monopolise the head of the list;
/// 60 is the value from Cormack, Clarke and Büttcher (2009) and is a documented default rather
/// than a tuned one.
/// </para>
/// <para>
/// <c>COALESCE(..., 0)</c> matters: the FULL OUTER JOIN means a row found by only one branch has
/// a NULL rank in the other, and NULL would propagate through the addition and erase the score
/// it did earn.
/// </para>
/// </summary>
internal sealed class RrfRankingStrategy(int k) : IRankingStrategy
{
    public string Name => "rrf";

    public string StatisticsCommonTableExpressions => string.Empty;

    public string StatisticsJoins => string.Empty;

    public string FusionExpression =>
        $"COALESCE(1.0 / ({k} + v.rank_position), 0.0) + COALESCE(1.0 / ({k} + t.rank_position), 0.0)";
}

/// <summary>
/// Weighted fusion over min-max normalised scores.
/// <para>
/// Uses the raw relevance scores rather than ranks, which preserves information RRF throws away:
/// the gap between the best and second-best match. That is useful when one branch is genuinely
/// far more confident, and useless — actively misleading — if the scores are not first brought
/// onto a common scale.
/// </para>
/// <para>
/// Hence the normalisation. Each branch's scores are rescaled to [0, 1] <b>within the candidate
/// set for this query</b>, which is what makes a weighted sum meaningful. Skipping this step is
/// the single most common error in hybrid search implementations, and it fails silently: the
/// results still look ranked.
/// </para>
/// <para>
/// The degenerate case — every candidate in a branch scoring identically, which includes the
/// extremely common case of exactly one candidate — leaves no range to normalise against and has
/// to be handled explicitly. <see cref="Normalise"/> documents why it resolves to 1.0.
/// </para>
/// </summary>
internal sealed class WeightedRankingStrategy(double vectorWeight, double textWeight) : IRankingStrategy
{
    public string Name => "weighted";

    public string StatisticsCommonTableExpressions => """
        , vector_stats AS (
            SELECT MIN(similarity) AS min_value, MAX(similarity) AS max_value FROM vector_ranked
        )
        , text_stats AS (
            SELECT MIN(score) AS min_value, MAX(score) AS max_value FROM text_ranked
        )
        """;

    public string StatisticsJoins => """
        CROSS JOIN vector_stats vs
        CROSS JOIN text_stats ts
        """;

    public string FusionExpression =>
        $"""
         {Literal(vectorWeight)} * {Normalise("v.similarity", "vs")}
         + {Literal(textWeight)} * {Normalise("t.score", "ts")}
         """;

    /// <summary>
    /// Min-max normalisation of one retrieval branch, with both edge cases stated rather than
    /// left to fall out of the arithmetic.
    /// <para>
    /// <b>Row not retrieved by this branch (NULL) → 0.</b> The FULL OUTER JOIN leaves NULL on
    /// whichever side did not find the row. Left alone, NULL propagates through the multiplication
    /// and the addition, and the entire fused score becomes NULL — so every result found by only
    /// one branch would sort last or vanish, which is precisely the population the vector branch
    /// exists to surface.
    /// </para>
    /// <para>
    /// <b>Retrieved, but the branch has no range (max = min) → 1.</b> This is the case that makes
    /// the obvious implementation wrong, and it is not exotic: it happens whenever a branch
    /// returns a single candidate. Guarding the division alone — <c>NULLIF(max - min, 0)</c> with
    /// a <c>COALESCE(..., 0)</c> around it — avoids the divide-by-zero and then scores that single
    /// candidate 0.0, identical to a row the branch never retrieved at all. A search matching
    /// exactly one item would report a relevance of zero for a perfect hit, and because the row is
    /// still returned and still ordered, nothing looks broken. 1.0 is the correct value: within
    /// this branch's candidate set the row is tied for best, and min-max normalisation maps the
    /// best candidate to 1.
    /// </para>
    /// <para>
    /// Worth noting what this does not fix, because it is inherent to min-max rather than a bug:
    /// with two or more distinct scores the lowest candidate in a branch always normalises to 0.
    /// That is the definition of the transform, and it is one of the reasons RRF is the default.
    /// </para>
    /// </summary>
    private static string Normalise(string column, string statistics) =>
        $"""
         CASE
             WHEN {column} IS NULL THEN 0.0
             WHEN {statistics}.max_value = {statistics}.min_value THEN 1.0
             ELSE ({column} - {statistics}.min_value) / ({statistics}.max_value - {statistics}.min_value)
         END
         """;

    private static string Literal(double weight) =>
        weight.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
