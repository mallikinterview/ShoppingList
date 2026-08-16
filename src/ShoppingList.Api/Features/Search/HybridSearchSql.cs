namespace ShoppingList.Api.Features.Search;

/// <summary>
/// The hybrid query: vector similarity, full-text search and metadata filters, fused in a single
/// statement.
/// <para>
/// <b>One round trip, not three.</b> The obvious implementation — run a vector query, run a text
/// query, merge the two lists in C# — transfers both candidate sets over the wire, gives up the
/// planner's ability to reason about the whole thing, and makes the fusion untestable against
/// real data. Doing it in SQL means Postgres reads only what it needs and returns only the final
/// page.
/// </para>
/// </summary>
internal static class HybridSearchSql
{
    /// <summary>
    /// Builds the statement for a ranking strategy.
    /// <para>
    /// The only interpolated fragments are the strategy's own CTEs, joins and fusion expression —
    /// all compile-time constants and configured numeric weights owned by this application.
    /// Every value originating from the request is a bound parameter. No user input reaches this
    /// string.
    /// </para>
    /// </summary>
    public static string Build(IRankingStrategy strategy) =>
        $"""
         WITH q AS (
             -- websearch_to_tsquery, not plainto_ or to_tsquery: it accepts what users actually
             -- type (quoted phrases, OR, leading minus) and, critically, never throws on
             -- malformed input. to_tsquery raises a syntax error on a stray operator, which
             -- turns a user typing "milk &" into a 500.
             SELECT websearch_to_tsquery('english', @query) AS tsq
         ),

         -- ── Branch 1: vector similarity ──────────────────────────────────────────────
         -- ORDER BY ... <=> ... LIMIT is applied directly against the base table, which is what
         -- lets the planner use the HNSW index. Wrapping the table in a filtering CTE first
         -- would materialise it and force a sequential scan — the single most common way an
         -- HNSW index ends up present but unused.
         --
         -- <=> is cosine DISTANCE, so smaller is better and the sort is ASC. Reversing it is a
         -- silent relevance inversion: results still come back, ranked exactly backwards.
         vector_candidates AS (
             SELECT i.id,
                    (i.embedding <=> @queryEmbedding) AS distance
             FROM shopping_items i
             WHERE i.user_id = @userId
               AND i.embedding IS NOT NULL
               AND @queryEmbedding IS NOT NULL
               -- Relevance floor. An ANN index returns the k nearest rows regardless of whether
               -- any of them are actually near, so without this every query matches the whole
               -- table on a small corpus. The predicate sits alongside the ORDER BY on the same
               -- expression, so the planner still drives the scan from the HNSW index and simply
               -- stops emitting rows once they fall outside the threshold.
               AND (i.embedding <=> @queryEmbedding) < @maxVectorDistance
               -- Metadata predicates are applied in BOTH branches. Filtering only after fusion
               -- would let each branch spend its candidate budget on rows that are about to be
               -- discarded, and the two branches would then be fusing over different populations.
               AND (@category IS NULL OR i.category = @category)
               AND (@isPurchased IS NULL OR i.is_purchased = @isPurchased)
             ORDER BY i.embedding <=> @queryEmbedding
             LIMIT @candidateLimit
         ),

         -- Ranking happens outside the candidate CTE. A window function in the same query block
         -- as ORDER BY ... LIMIT is evaluated over every matching row before the limit applies,
         -- which would defeat the index scan entirely.
         vector_ranked AS (
             SELECT id,
                    ROW_NUMBER() OVER (ORDER BY distance ASC) AS rank_position,
                    (1.0 - distance) AS similarity
             FROM vector_candidates
         ),

         -- ── Branch 2: full-text search ───────────────────────────────────────────────
         -- search_vector is a stored generated column with a GIN index. Computing
         -- to_tsvector(...) inline here instead would make the index unusable and sequentially
         -- scan the table on every search.
         text_candidates AS (
             SELECT i.id,
                    ts_rank_cd(i.search_vector, q.tsq) AS score
             FROM shopping_items i
             CROSS JOIN q
             WHERE i.user_id = @userId
               AND i.search_vector @@ q.tsq
               AND (@category IS NULL OR i.category = @category)
               AND (@isPurchased IS NULL OR i.is_purchased = @isPurchased)
             ORDER BY ts_rank_cd(i.search_vector, q.tsq) DESC
             LIMIT @candidateLimit
         ),

         text_ranked AS (
             SELECT id,
                    ROW_NUMBER() OVER (ORDER BY score DESC) AS rank_position,
                    score
             FROM text_candidates
         )
         {strategy.StatisticsCommonTableExpressions}

         -- ── Fusion ───────────────────────────────────────────────────────────────────
         -- FULL OUTER JOIN, not INNER. An item that matches semantically but shares no keyword
         -- with the query — "dish soap" for "washing up liquid" — appears only in the vector
         -- branch, and an INNER JOIN would discard exactly the results that justify having a
         -- vector branch at all. The reverse case is just as real: exact keyword matches on
         -- items whose embedding has not been generated yet.
         , fused AS (
             SELECT COALESCE(v.id, t.id) AS id,
                    {strategy.FusionExpression} AS fused_score,
                    v.similarity     AS vector_similarity,
                    t.score          AS text_score,
                    v.rank_position  AS vector_rank,
                    t.rank_position  AS text_rank
             FROM vector_ranked v
             FULL OUTER JOIN text_ranked t ON v.id = t.id
             {strategy.StatisticsJoins}
         )

         SELECT i.id,
                i.name,
                i.notes,
                i.quantity,
                i.unit,
                i.category,
                i.is_purchased,
                i.created_at,
                f.fused_score,
                f.vector_similarity,
                f.text_score,
                f.vector_rank,
                f.text_rank
         FROM fused f
         JOIN shopping_items i ON i.id = f.id
         -- created_at breaks score ties deterministically. Without a tiebreaker, equally scored
         -- rows come back in whatever order the executor produces them, so pagination can repeat
         -- or skip items between pages and any ranking assertion in a test is flaky.
         ORDER BY f.fused_score DESC, i.created_at DESC, i.id DESC
         LIMIT @limit
         OFFSET @offset;
         """;
}
