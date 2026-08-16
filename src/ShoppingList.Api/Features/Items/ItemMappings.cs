using ShoppingList.Api.Data.Entities;
using ShoppingList.Api.Infrastructure.Storage;

namespace ShoppingList.Api.Features.Items;

/// <summary>
/// Hand-written mapping.
/// <para>
/// No AutoMapper. Reflection-based mapping trades a few lines of obvious code for configuration
/// that fails at runtime rather than compile time, a startup cost, and mappings that silently
/// stop matching when a property is renamed. It also now carries a commercial licence. Twenty
/// lines here are debuggable, greppable and free.
/// </para>
/// <para>
/// Image URLs are presigned per response rather than stored: a presigned URL is a time-limited
/// credential, so persisting or caching one produces links that expire while still being served.
/// </para>
/// </summary>
internal static class ItemMappings
{
    public static async Task<ItemResponse> ToResponseAsync(
        this ShoppingItem item,
        IObjectStorage storage,
        CancellationToken ct)
    {
        var images = new List<ItemImageResponse>(item.Images.Count);

        foreach (var image in item.Images.OrderBy(i => i.CreatedAt))
        {
            images.Add(new ItemImageResponse(
                image.Id,
                image.ContentType,
                image.SizeBytes,
                image.OriginalFileName,
                await storage.GetPresignedDownloadUrlAsync(image.ObjectKey, ct),
                image.CreatedAt));
        }

        return new ItemResponse(
            item.Id,
            item.Name,
            item.Notes,
            item.Quantity,
            item.Unit,
            item.Category,
            item.IsPurchased,
            item.EmbeddingStatus.ToString(),
            images,
            item.CreatedAt,
            item.UpdatedAt);
    }
}
