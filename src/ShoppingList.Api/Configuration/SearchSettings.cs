using System.ComponentModel.DataAnnotations;

namespace ShoppingList.Api.Configuration;

public sealed class SearchSettings
{
    public const string SectionName = "SearchSettings";

    /// <summary>
    /// Default ranking strategy when no experiment is running. See <see cref="RankingStrategy"/>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(rrf|weighted)$", ErrorMessage = "Strategy must be 'rrf' or 'weighted'.")]
    public string Strategy { get; init; } = "rrf";

    /// <summary>
    /// The <c>k</c> constant in Reciprocal Rank Fusion: <c>score = Σ 1 / (k + rank)</c>.
    /// Higher values flatten the contribution of top ranks. 60 is the value from the original
    /// Cormack et al. paper and is a reasonable default rather than a tuned one.
    /// </summary>
    [Range(1, 1000)]
    public int RrfK { get; init; } = 60;

    /// <summary>Weight applied to normalised vector similarity under the 'weighted' strategy.</summary>
    [Range(0.0, 1.0)]
    public double VectorWeight { get; init; } = 0.6;

    /// <summary>Weight applied to normalised full-text rank under the 'weighted' strategy.</summary>
    [Range(0.0, 1.0)]
    public double TextWeight { get; init; } = 0.4;

    /// <summary>
    /// Rows drawn from each retrieval branch before fusion. Fusion can only reorder what it is
    /// given, so this bounds recall; raising it costs latency in both index scans.
    /// </summary>
    [Range(1, 500)]
    public int CandidateLimit { get; init; } = 50;

    [Range(1, 200)]
    public int MaxPageSize { get; init; } = 50;

    /// <summary>
    /// Maximum cosine distance for a vector candidate to be considered a match at all.
    /// <para>
    /// Without a floor, an approximate-nearest-neighbour index does exactly what its name says:
    /// it returns the k nearest rows whether or not any of them are near. On a small corpus that
    /// means every search returns the entire table, ranked — technically correct fusion over a
    /// meaningless candidate set. Searching for "dish soap" would return bread.
    /// </para>
    /// <para>
    /// Cosine distance runs 0 (identical direction) to 2 (opposite). 0.48 was measured with the
    /// floor opened wide so nothing was hidden. Under <c>nomic-embed-text</c>, relevant
    /// query-to-item pairs ran from 0.276 to 0.474 and irrelevant pairs started at 0.504, so
    /// 0.48 sits in that gap — every query tested returned its relevant items and no others.
    /// </para>
    /// <para>
    /// The separation does not hold beyond that point, and pretending otherwise would be the
    /// mistake worth avoiding. "Strawberry jam" for the query "something to put on toast" scores
    /// 0.551 — further away than "Dish soap" for the same query at 0.529. On a small corpus of
    /// two-word names the model does not rank those apart, so no threshold admits the one and
    /// rejects the other. This value therefore chooses precision over recall: fewer results, but
    /// never nonsense. Recall is recovered by the full-text branch, which finds exact words the
    /// vector branch ranks low — which is the argument for hybrid retrieval rather than either
    /// method alone.
    /// </para>
    /// <para>
    /// The number does not transfer. It is specific to this embedding model and this corpus, and
    /// re-measuring is part of changing either — which is why it is configuration and not a
    /// constant.
    /// </para>
    /// <para>
    /// It stays configurable because the right number depends on the embedding model and the
    /// corpus, and tuning it is exactly the kind of change the ranking experiment exists to
    /// evaluate on live traffic.
    /// </para>
    /// </summary>
    [Range(0.0, 2.0)]
    public double MaxVectorDistance { get; init; } = 0.48;

    public ExperimentSettings Experiment { get; init; } = new();
}

/// <summary>
/// Ranking experiment configuration. This is deliberately assignment-only: there is no
/// experiment store, no exposure event pipeline and no significance testing. What it does
/// provide is the two things that are easy to get wrong and expensive to discover later —
/// assignment that is sticky per user, and cache keys that are partitioned by variant.
/// </summary>
public sealed class ExperimentSettings
{
    public bool Enabled { get; init; }

    /// <summary>
    /// Salts the assignment hash. Changing it reshuffles every user into a fresh assignment,
    /// which is how a new experiment starts without inheriting the previous one's population.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Key { get; init; } = "search-ranking-v1";

    /// <summary>Percentage of users assigned to the treatment variant.</summary>
    [Range(0, 100)]
    public int VariantSplit { get; init; } = 50;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(rrf|weighted)$")]
    public string ControlStrategy { get; init; } = "rrf";

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(rrf|weighted)$")]
    public string TreatmentStrategy { get; init; } = "weighted";
}
