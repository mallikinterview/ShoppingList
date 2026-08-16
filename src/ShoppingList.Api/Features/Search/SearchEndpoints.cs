using Microsoft.AspNetCore.Mvc;
using ShoppingList.Api.Common.Validation;
using ShoppingList.Api.Experimentation;
using ShoppingList.Api.Infrastructure.Identity;

namespace ShoppingList.Api.Features.Search;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/search")
            .WithTags("Search")
            // Authorized like every other data endpoint. Search is the one people forget to
            // scope, and an unscoped search endpoint leaks the entire corpus regardless of how
            // carefully the CRUD endpoints are protected.
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Standard);

        group.MapPost("/", SearchAsync)
            .WithName("HybridSearch")
            .WithSummary("Hybrid search: vector similarity + full-text + metadata filters")
            .WithDescription(
                "Runs one SQL statement combining pgvector cosine similarity, PostgreSQL " +
                "full-text search and metadata predicates, fused by the configured ranking " +
                "strategy. Results are scoped to the authenticated user by a database-level " +
                "query filter. If the embedding service is unavailable the search degrades to " +
                "keyword-only rather than failing; the response's vectorSearchUsed flag reports " +
                "which happened.")
            .WithValidation<SearchRequest>()
            .Produces<SearchResponse>()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> SearchAsync(
        [FromBody] SearchRequest request,
        IHybridSearchService search,
        ICurrentUser currentUser,
        HttpContext context,
        CancellationToken ct)
    {
        var response = await search.SearchAsync(request, currentUser.UserId, ct);

        // Surfaced as a header as well as in the body so the assignment is visible to anything
        // sitting in front of the API — a proxy, a browser devtools panel, a curl -i — without
        // having to parse the payload.
        context.Response.Headers["X-Experiment-Variant"] = response.Diagnostics.Variant;
        context.Response.Headers["X-Ranking-Strategy"] = response.Diagnostics.Strategy;

        return TypedResults.Ok(response);
    }
}

public static class SearchExtensions
{
    public static IServiceCollection AddSearch(this IServiceCollection services)
    {
        services.AddScoped<IHybridSearchService, HybridSearchService>();
        services.AddSingleton<IVariantAssigner, VariantAssigner>();

        return services;
    }
}
