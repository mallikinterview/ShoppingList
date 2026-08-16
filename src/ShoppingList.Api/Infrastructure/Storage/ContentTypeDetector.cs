namespace ShoppingList.Api.Infrastructure.Storage;

/// <summary>
/// Determines an image's real type by inspecting its leading bytes.
/// <para>
/// The uploaded <c>Content-Type</c> header and the file extension are both attacker-controlled
/// strings — a caller can send an HTML page, a script, or an executable labelled
/// <c>image/png</c>. Trusting either is how a file-upload endpoint becomes a stored-XSS or
/// malware-hosting vector. Magic bytes are the actual file's own claim about itself.
/// </para>
/// <para>
/// An allowlist, not a blocklist: unknown formats are rejected. A blocklist is a promise to have
/// thought of every dangerous format, which is not a promise anyone can keep.
/// </para>
/// </summary>
internal static class ContentTypeDetector
{
    public const int RequiredHeaderBytes = 12;

    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length < RequiredHeaderBytes)
        {
            return null;
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return "image/png";
        }

        // JPEG: FF D8 FF
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        // GIF: "GIF87a" or "GIF89a"
        if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38 &&
            (header[4] == 0x37 || header[4] == 0x39) && header[5] == 0x61)
        {
            return "image/gif";
        }

        // WebP: "RIFF" .... "WEBP"
        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
        {
            return "image/webp";
        }

        // Deliberately absent: SVG. It is an image format that executes script, so accepting it
        // and serving it back would be a stored-XSS vector. It is also textual, so it has no
        // reliable magic number to detect in the first place.
        return null;
    }

    public static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".bin"
    };
}
