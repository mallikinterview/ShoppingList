using System.Globalization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using ShoppingList.Api.Configuration;

namespace ShoppingList.Api.Common.Extensions;

/// <summary>
/// Publishes the constraints that <c>GET /api/v1/items</c> actually enforces on its query string.
/// <para>
/// <c>ValidationSchemaTransformer</c> derives constraints from FluentValidation validators, which
/// covers every request body. Query parameters bind as loose method arguments rather than to a
/// validated DTO, so their rules live in the endpoint body instead — and the generator has no way
/// to see them. Left alone, the document advertises an unbounded <c>pageSize</c> and an unbounded
/// <c>category</c> while the endpoint rejects both with 400. A contract that under-states its own
/// rules is worse than one that states none: it invites the client to send something the server
/// has already decided to refuse.
/// </para>
/// <para>
/// The page-size bound is read from configuration rather than written here, so the document and
/// the check cannot drift apart when <c>MaxPageSize</c> is retuned.
/// </para>
/// </summary>
internal sealed class ItemQueryParameterTransformer : IOpenApiOperationTransformer
{
    /// <summary>
    /// Mirrors the guard in <c>ItemEndpoints.ListAsync</c>, which in turn mirrors the 64-character
    /// cap the category validators apply on write. Filtering by a value longer than any storable
    /// category can only ever return nothing.
    /// </summary>
    private const int MaxCategoryLength = 64;

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Matched on the operation id set by WithName("ListItems") rather than on the route
        // template, so a path change does not silently stop the constraints being published.
        if (!string.Equals(operation.OperationId, "ListItems", StringComparison.Ordinal)
            || operation.Parameters is null)
        {
            return Task.CompletedTask;
        }

        var maxPageSize = context.ApplicationServices
            .GetRequiredService<IOptions<SearchSettings>>()
            .Value.MaxPageSize;

        foreach (var parameter in operation.Parameters)
        {
            if (parameter as OpenApiParameter is not { } target)
            {
                continue;
            }

            var schema = target.Schema as OpenApiSchema;

            switch (target.Name)
            {
                case "pageSize":
                    target.Description =
                        $"Items per page. Must be between 1 and {maxPageSize}. A value outside "
                        + "that range is rejected with 400 rather than silently clamped, so a "
                        + "caller never receives a page size different from the one requested "
                        + "without being told.";

                    if (schema is not null)
                    {
                        // Numeric bounds are strings in Microsoft.OpenApi v2: JSON Schema 2020-12
                        // allows arbitrary-precision numbers, which no CLR numeric type can hold.
                        schema.Minimum = "1";
                        schema.Maximum = maxPageSize.ToString(CultureInfo.InvariantCulture);
                    }

                    break;

                case "category":
                    target.Description =
                        $"Exact-match category filter, {MaxCategoryLength} characters or fewer. "
                        + "Omit to return every category.";

                    if (schema is not null)
                    {
                        schema.MaxLength = MaxCategoryLength;
                    }

                    break;

                case "cursor":
                    // Deliberately carries no maxLength. The endpoint does not check the length —
                    // it attempts to decode and returns 400 on failure — so publishing a bound
                    // here would introduce exactly the mismatch this transformer exists to remove.
                    target.Description =
                        "Opaque keyset cursor. Pass the nextCursor from the previous response; "
                        + "omit it for the first page. Cursors are positional, not numbered, so "
                        + "pagination stays correct while rows are being inserted.";
                    break;

                case "isPurchased":
                    target.Description =
                        "Filter by purchased state. Omit to return both purchased and outstanding "
                        + "items.";
                    break;

                default:
                    break;
            }
        }

        return Task.CompletedTask;
    }
}
