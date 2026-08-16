namespace ShoppingList.Api.Data.Entities;

/// <summary>
/// Metadata for an image held in object storage. The database row is the source of truth for
/// whether an image exists; the object is the payload.
/// </summary>
public sealed class ItemImage
{
    private ItemImage() { }

    public Guid Id { get; private set; }

    public Guid ItemId { get; private set; }

    public ShoppingItem Item { get; private set; } = null!;

    /// <summary>
    /// Object key in Minio, always <c>{userId}/{itemId}/{guid}{ext}</c>. Every segment is
    /// server-generated. Nothing from the client's filename reaches this value, which is what
    /// makes path traversal and key collision impossible rather than merely unlikely.
    /// </summary>
    public string ObjectKey { get; private set; } = string.Empty;

    /// <summary>Content type as determined by inspecting the file's magic bytes — never the
    /// client-supplied header, which is trivially forged.</summary>
    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>
    /// Retained for display only, sanitised on the way in. Never used to build the object key,
    /// a filesystem path, or a response header.
    /// </summary>
    public string? OriginalFileName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ItemImage Create(
        Guid itemId,
        string objectKey,
        string contentType,
        long sizeBytes,
        string? originalFileName) => new()
        {
            Id = Guid.CreateVersion7(),
            ItemId = itemId,
            ObjectKey = objectKey,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            OriginalFileName = Sanitise(originalFileName)
        };

    private static string? Sanitise(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // Path components stripped: a value like "../../etc/passwd" or a Windows path is only
        // ever displayed, but stripping it here means no future consumer can misuse it.
        var name = Path.GetFileName(fileName.Replace('\\', '/'));

        var cleaned = new string(name
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ')
            .ToArray())
            .Trim();

        return cleaned.Length switch
        {
            0 => null,
            > 128 => cleaned[..128],
            _ => cleaned
        };
    }
}
