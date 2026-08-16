using FluentValidation;
using ShoppingList.Api.Common.Extensions;

namespace ShoppingList.Api.Features.Search;

public sealed record SearchRequest(
    // Deliberately a phrase that shares no keyword with any item name. Under keyword search alone
    // it returns nothing; under hybrid search it returns bread and jam. The example is therefore
    // a demonstration of what the endpoint is for, not just a syntactically valid value.
    [property: OpenApiExample("something to put on toast")] string Query,
    [property: OpenApiExample("Dairy")] string? Category,
    [property: OpenApiExample(false)] bool? IsPurchased,
    int Limit = 20,
    int Offset = 0);

public sealed record SearchHit(
    Guid Id,
    string Name,
    string? Notes,
    int Quantity,
    string? Unit,
    string? Category,
    bool IsPurchased,
    DateTimeOffset CreatedAt,
    // Component scores are returned alongside the fused score deliberately: without them a
    // ranking is unexplainable, and "why did this rank above that" is the first question anyone
    // asks of a relevance change. It also makes the fusion arithmetic verifiable from outside.
    double Score,
    double? VectorSimilarity,
    double? TextScore,
    int? VectorRank,
    int? TextRank);

public sealed record SearchResponse(
    IReadOnlyList<SearchHit> Results,
    int Count,
    SearchDiagnostics Diagnostics);

/// <summary>
/// Returned with every search so the caller — and the reviewer — can see exactly how the result
/// set was produced: which variant they were assigned, which strategy that maps to, and whether
/// vector recall actually participated.
/// </summary>
public sealed record SearchDiagnostics(
    string Variant,
    string Strategy,
    bool VectorSearchUsed,
    bool Cached,
    double DurationMs);

internal sealed class SearchRequestValidator : AbstractValidator<SearchRequest>
{
    public SearchRequestValidator()
    {
        RuleFor(x => x.Query)
            .NotEmpty()
            // Bounded because the query is embedded — an unbounded string is an unbounded
            // inference cost, and one request could occupy the model server indefinitely.
            .MaximumLength(500)
            .WithMessage("Search query must be 500 characters or fewer.");

        RuleFor(x => x.Category).MaximumLength(64);

        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(50);

        RuleFor(x => x.Offset)
            .GreaterThanOrEqualTo(0)
            // Offset is capped rather than unlimited. Deep pagination over a fused result set is
            // not meaningful — fusion only reorders the candidate window, so page 100 of a
            // 50-candidate retrieval is empty by construction. Documented rather than pretended.
            .LessThanOrEqualTo(500);
    }
}
