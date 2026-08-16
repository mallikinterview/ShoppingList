using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShoppingList.Api.Common.Errors;
using ShoppingList.Api.Common.Pagination;
using ShoppingList.Api.Common.Validation;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Data;
using ShoppingList.Api.Data.Entities;
using ShoppingList.Api.Infrastructure.Caching;
using ShoppingList.Api.Infrastructure.Embeddings;
using ShoppingList.Api.Infrastructure.Identity;
using ShoppingList.Api.Infrastructure.Storage;

namespace ShoppingList.Api.Features.Items;

public static class ItemEndpoints
{
    public static IEndpointRouteBuilder MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/items")
            .WithTags("Shopping items")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Standard);

        group.MapPost("/", CreateAsync)
            .WithName("CreateItem")
            .WithSummary("Create a shopping list item")
            .WithValidation<CreateItemRequest>()
            .Produces<ItemResponse>(StatusCodes.Status201Created);

        group.MapGet("/", ListAsync)
            .WithName("ListItems")
            .WithSummary("List the caller's items (keyset pagination)")
            .Produces<PagedResult<ItemResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetItem")
            .WithSummary("Get a single item")
            .Produces<ItemResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateItem")
            .WithSummary("Replace an item")
            .WithValidation<UpdateItemRequest>()
            .Produces<ItemResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteItem")
            .WithSummary("Delete an item and any attached images")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateItemRequest request,
        AppDbContext db,
        ICurrentUser currentUser,
        IEmbeddingQueue embeddingQueue,
        IItemCache cache,
        IObjectStorage storage,
        CancellationToken ct)
    {
        var item = ShoppingItem.Create(
            currentUser.UserId,
            request.Name,
            request.Notes,
            request.Quantity,
            request.Unit,
            request.Category);

        db.Items.Add(item);
        await db.SaveChangesAsync(ct);

        // Queued after the commit, never before. Embedding a row that then fails to persist
        // leaves the worker chasing an id that does not exist; embedding after the commit at
        // worst leaves the item Pending until the reconciliation sweep picks it up.
        await embeddingQueue.EnqueueAsync(item.Id, ct);

        await cache.InvalidateUserAsync(currentUser.UserId, ct);

        return TypedResults.Created(
            $"/api/v1/items/{item.Id}",
            await item.ToResponseAsync(storage, ct));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        AppDbContext db,
        IObjectStorage storage,
        ICurrentUser currentUser,
        IItemCache cache,
        CancellationToken ct)
    {
        // Cache-aside on the read path. Every write for this user bumps a version stamp, so an
        // update or delete makes the cached copy unreachable immediately rather than leaving it
        // to expire — the invalidation is not best-effort.
        //
        // Caching the mapped response, not the entity: the response carries presigned image URLs
        // that expire in 900 seconds, and the cache TTL is 300 plus at most 60 seconds of jitter.
        // A cached response therefore always outlives none of its URLs. Were that ever reversed,
        // this would serve links that 403 on arrival, so the two numbers belong together in
        // .env.example where the relationship can be seen.
        var response = await cache.GetOrCreateAsync(
            CacheKeys.Item(currentUser.UserId, id),
            currentUser.UserId,
            async token =>
            {
                // No ownership predicate here, and none is needed: the global query filter appends
                // `WHERE user_id = @currentUser` to every query against this set. An item belonging
                // to another user is not "found and rejected" — it is not visible to this query at
                // all.
                var item = await db.Items
                    .Include(i => i.Images)
                    .FirstOrDefaultAsync(i => i.Id == id, token)
                    // Thrown from inside the factory, so nothing is written to the cache. A cached
                    // 404 would outlive the condition that caused it: create an item with an id a
                    // client had already probed and the miss would persist until the TTL expired.
                    ?? throw new NotFoundException($"Item '{id}' was not found.");

                return await item.ToResponseAsync(storage, token);
            },
            ct);

        // 404 rather than 403 when the item exists but belongs to someone else. A 403 confirms
        // the id is real, which turns this endpoint into an enumeration oracle.
        return response is null
            ? throw new NotFoundException($"Item '{id}' was not found.")
            : TypedResults.Ok(response);
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        IObjectStorage storage,
        IOptions<SearchSettings> searchOptions,
        ICurrentUser currentUser,
        IItemCache cache,
        CancellationToken ct,
        [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? category = null,
        [FromQuery] bool? isPurchased = null)
    {
        // Rejected, not clamped. Quietly rewriting a caller's pageSize means the response does
        // not answer the question that was asked and the caller has no way to tell. An
        // unbounded pageSize is still a trivial denial-of-service, so the bound stays — it is
        // enforced with a 400 rather than a silent substitution, which is what every other
        // endpoint here already does with bad input.
        var maxPageSize = searchOptions.Value.MaxPageSize;

        if (pageSize < 1 || pageSize > maxPageSize)
        {
            throw new BadRequestException($"'pageSize' must be between 1 and {maxPageSize}.");
        }

        // 64 matches RuleFor(x => x.Category).MaximumLength(64) in ItemContracts.cs, so a
        // category that cannot be created also cannot be filtered on.
        if (category is { Length: > 64 })
        {
            throw new BadRequestException("'category' must be 64 characters or fewer.");
        }

        (DateTimeOffset CreatedAt, Guid Id)? anchor = null;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            // Restarting from page 1 on a corrupted cursor is worse than failing: a client
            // paginating in a loop never terminates, it re-reads the first page forever.
            if (!Cursor.TryDecode(cursor, out var decodedAt, out var decodedId))
            {
                throw new BadRequestException("'cursor' is not a valid pagination cursor.");
            }

            anchor = (decodedAt, decodedId);
        }

        // Cached after validation, never before: a rejected request must not occupy a cache key,
        // and a 400 must stay a 400 on every attempt rather than being answered from a hit.
        //
        // isPurchased needs no validation of its own. It binds as bool?, so anything that is not
        // a boolean fails model binding and the framework answers 400 before this method runs —
        // adding a check here would duplicate a rule the type system already enforces. It is
        // recorded rather than left silent because "no validation" and "validation elsewhere"
        // look identical to a reader.
        var result = await cache.GetOrCreateAsync(
            CacheKeys.ItemList(currentUser.UserId, cursor, pageSize, category, isPurchased),
            currentUser.UserId,
            async token => await LoadPageAsync(db, storage, anchor, pageSize, category, isPurchased, token),
            ct);

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// The uncached read. Separated from <c>ListAsync</c> so the cache wrapper reads as one
    /// statement and the query is not indented three levels inside a lambda.
    /// </summary>
    private static async Task<PagedResult<ItemResponse>> LoadPageAsync(
        AppDbContext db,
        IObjectStorage storage,
        (DateTimeOffset CreatedAt, Guid Id)? anchor,
        int pageSize,
        string? category,
        bool? isPurchased,
        CancellationToken ct)
    {
        var query = db.Items
            .Include(i => i.Images)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(i => i.Category == category);
        }

        if (isPurchased.HasValue)
        {
            query = query.Where(i => i.IsPurchased == isPurchased.Value);
        }

        // Keyset predicate on the same (CreatedAt, Id) tuple the index is ordered by, so the
        // planner seeks straight to the position instead of counting rows to skip.
        if (anchor is { } keyset)
        {
            // Lifted out of the expression tree: EF parameterises captured locals cleanly,
            // whereas tuple member access inside the predicate is needless work for the
            // translator.
            var anchorCreatedAt = keyset.CreatedAt;
            var anchorId = keyset.Id;

            query = query.Where(i =>
                i.CreatedAt < anchorCreatedAt || (i.CreatedAt == anchorCreatedAt && i.Id.CompareTo(anchorId) < 0));
        }

        // One extra row is fetched purely to answer "is there another page" without a COUNT,
        // which on a large table costs a full scan.
        var items = await query
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var responses = new List<ItemResponse>(items.Count);
        foreach (var item in items)
        {
            responses.Add(await item.ToResponseAsync(storage, ct));
        }

        var nextCursor = hasMore && items.Count > 0
            ? Cursor.Encode(items[^1].CreatedAt, items[^1].Id)
            : null;

        return new PagedResult<ItemResponse>(responses, nextCursor, pageSize);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        [FromBody] UpdateItemRequest request,
        AppDbContext db,
        ICurrentUser currentUser,
        IEmbeddingQueue embeddingQueue,
        IItemCache cache,
        IObjectStorage storage,
        CancellationToken ct)
    {
        // AsTracking, because the default for this context is NoTracking. Explicit opt-in on
        // the two write paths is safer than tracking everything and hoping reads opt out.
        var item = await db.Items
            .AsTracking()
            .Include(i => i.Images)
            .FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException($"Item '{id}' was not found.");

        item.Update(
            request.Name,
            request.Notes,
            request.Quantity,
            request.Unit,
            request.Category,
            request.IsPurchased);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer changed the row since it was read. Surfaced as 409 so the client
            // can re-read and retry — the alternative is silently discarding their edit.
            throw new ConflictException(
                "This item was modified by another request. Re-read it and apply your change again.");
        }

        // Editing the name or notes clears the embedding (see ShoppingItem.Update), so the item
        // must be re-queued or it stays keyword-only forever.
        if (item.EmbeddingStatus == EmbeddingStatus.Pending)
        {
            await embeddingQueue.EnqueueAsync(item.Id, ct);
        }

        await cache.InvalidateUserAsync(currentUser.UserId, ct);

        return TypedResults.Ok(await item.ToResponseAsync(storage, ct));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        AppDbContext db,
        ICurrentUser currentUser,
        IItemCache cache,
        IObjectStorage storage,
        ILogger<ShoppingItem> logger,
        CancellationToken ct)
    {
        var item = await db.Items
            .AsTracking()
            .Include(i => i.Images)
            .FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException($"Item '{id}' was not found.");

        var objectKeys = item.Images.Select(i => i.ObjectKey).ToArray();

        db.Items.Remove(item);
        await db.SaveChangesAsync(ct);

        // Objects are removed after the database commit, and a failure here is logged rather
        // than thrown. The row is already gone, so the delete succeeded from the caller's point
        // of view; failing the response would tell them otherwise. The cost is an orphaned
        // object, which is recorded in Known Limitations along with the reconciliation sweep
        // that would clean it up.
        foreach (var key in objectKeys)
        {
            try
            {
                await storage.DeleteAsync(key, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Item {ItemId} was deleted but its object {ObjectKey} could not be removed; it is now orphaned.",
                    id, key);
            }
        }

        await cache.InvalidateUserAsync(currentUser.UserId, ct);

        return TypedResults.NoContent();
    }
}
