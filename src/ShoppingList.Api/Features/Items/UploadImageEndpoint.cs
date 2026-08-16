using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShoppingList.Api.Common.Errors;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Data;
using ShoppingList.Api.Data.Entities;
using ShoppingList.Api.Infrastructure.Caching;
using ShoppingList.Api.Infrastructure.Identity;
using ShoppingList.Api.Infrastructure.Storage;

namespace ShoppingList.Api.Features.Items;

public static class UploadImageEndpoint
{
    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/items/{itemId:guid}/images")
            .WithTags("Shopping items")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Upload)
            .DisableAntiforgery();

        group.MapPost("/", UploadAsync)
            .WithName("UploadItemImage")
            .WithSummary("Attach an image to an item")
            .WithDescription(
                "Content type is determined by inspecting the file's magic bytes, not the " +
                "supplied Content-Type header or extension. Images are stored in Minio under a " +
                "server-generated, user-namespaced key and served through short-lived presigned URLs. " +
                "The canonical form field is 'file'; if a client sends the upload under a different " +
                "field name, the first non-empty file part is used instead.")
            .Produces<ItemImageResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapDelete("/{imageId:guid}", DeleteAsync)
            .WithName("DeleteItemImage")
            .WithSummary("Remove an image from an item")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> UploadAsync(
        Guid itemId,
        // Nullable, and paired with the raw form below, because the field name cannot be relied
        // upon. Some OpenAPI clients — Scalar's multipart control among them — submit the binary
        // under a part named after the local filename while still emitting an empty part called
        // 'file'. Binding only the declared name would then see zero bytes and reject a request
        // that plainly carries an image. See the resolution immediately below.
        IFormFile? file,
        HttpRequest request,
        AppDbContext db,
        IObjectStorage storage,
        ICurrentUser currentUser,
        IItemCache cache,
        IOptions<MinioSettings> options,
        ILogger<ItemImage> logger,
        CancellationToken ct)
    {
        var settings = options.Value;

        // The item is loaded through the global query filter first. Somebody else's item is
        // simply not visible here, so an upload can never be attached to it — the ownership
        // check and the existence check are the same query.
        var item = await db.Items
            .AsTracking()
            .FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new NotFoundException($"Item '{itemId}' was not found.");

        // Strict in what it sends, liberal in what it accepts. The declared field wins when it
        // carries content; otherwise the first part that actually has bytes is taken. This is
        // tolerance at the boundary only — nothing downstream trusts the field name, the file
        // name or the declared content type, so accepting a differently-named part widens no
        // attack surface. ReadFormAsync returns the already-parsed form rather than re-reading
        // the body.
        var form = await request.ReadFormAsync(ct);

        var upload = file is { Length: > 0 }
            ? file
            : form.Files.FirstOrDefault(f => f.Length > 0);

        if (upload is null)
        {
            throw new BadRequestException(
                "No image was received. Attach a non-empty file to the form field named 'file'.");
        }

        // Checked before reading a single byte. Validating after buffering would mean an
        // attacker can force the server to hold an arbitrarily large payload in memory just to
        // be told it is too large.
        if (upload.Length > settings.MaxUploadBytes)
        {
            return TypedResults.Problem(
                title: "File too large",
                detail: $"Maximum upload size is {settings.MaxUploadBytes / 1024 / 1024} MB.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        await using var stream = upload.OpenReadStream();

        // Only the header is read, and the stream is then rewound for upload — the file itself
        // is streamed to storage rather than being materialised in memory.
        var header = new byte[ContentTypeDetector.RequiredHeaderBytes];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);

        var detectedContentType = ContentTypeDetector.Detect(header.AsSpan(0, read));

        if (detectedContentType is null)
        {
            // Deliberately does not echo the client's claimed Content-Type. It is untrusted
            // input, and reflecting it into a response is how a rejection message becomes an
            // injection vector.
            logger.LogWarning("Rejected upload for item {ItemId}: content did not match any allowed image format.", itemId);

            throw new BadRequestException(
                "The uploaded file is not a supported image. Allowed formats: PNG, JPEG, GIF, WebP.");
        }

        stream.Position = 0;

        var objectKey = IObjectStorage.BuildKey(currentUser.UserId, item.Id, detectedContentType);

        await storage.UploadAsync(objectKey, stream, detectedContentType, upload.Length, ct);

        var image = ItemImage.Create(item.Id, objectKey, detectedContentType, upload.Length, upload.FileName);
        db.Images.Add(image);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // The object is already in storage but the metadata row failed. Without this the
            // object would be orphaned with nothing referencing it and no way to find it again.
            // A reserve-then-confirm two-phase upload would close the remaining window; that
            // tradeoff is recorded in Known Limitations rather than left implicit.
            await storage.DeleteAsync(objectKey, ct);
            throw;
        }

        await cache.InvalidateUserAsync(currentUser.UserId, ct);

        var url = await storage.GetPresignedDownloadUrlAsync(objectKey, ct);

        return TypedResults.Created(
            $"/api/v1/items/{itemId}/images/{image.Id}",
            new ItemImageResponse(
                image.Id, image.ContentType, image.SizeBytes, image.OriginalFileName, url, image.CreatedAt));
    }

    private static async Task<IResult> DeleteAsync(
        Guid itemId,
        Guid imageId,
        AppDbContext db,
        IObjectStorage storage,
        ICurrentUser currentUser,
        IItemCache cache,
        CancellationToken ct)
    {
        var image = await db.Images
            .AsTracking()
            .FirstOrDefaultAsync(i => i.Id == imageId && i.ItemId == itemId, ct)
            ?? throw new NotFoundException($"Image '{imageId}' was not found.");

        var objectKey = image.ObjectKey;

        db.Images.Remove(image);
        await db.SaveChangesAsync(ct);

        await storage.DeleteAsync(objectKey, ct);
        await cache.InvalidateUserAsync(currentUser.UserId, ct);

        return TypedResults.NoContent();
    }
}
