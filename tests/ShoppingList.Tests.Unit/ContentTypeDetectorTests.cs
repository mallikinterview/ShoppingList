using ShoppingList.Api.Infrastructure.Storage;

namespace ShoppingList.Tests.Unit;

public sealed class ContentTypeDetectorTests
{
    [Fact]
    public void Detects_png() =>
        ContentTypeDetector.Detect(Header([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            .Should().Be("image/png");

    [Fact]
    public void Detects_jpeg() =>
        ContentTypeDetector.Detect(Header([0xFF, 0xD8, 0xFF, 0xE0]))
            .Should().Be("image/jpeg");

    [Fact]
    public void Detects_gif() =>
        ContentTypeDetector.Detect(Header("GIF89a"u8.ToArray()))
            .Should().Be("image/gif");

    [Fact]
    public void Detects_webp()
    {
        var header = new byte[12];
        "RIFF"u8.CopyTo(header);
        "WEBP"u8.CopyTo(header.AsSpan(8));

        ContentTypeDetector.Detect(header).Should().Be("image/webp");
    }

    [Fact]
    public void Rejects_html_disguised_as_an_image()
    {
        // The attack this whole class exists to stop: a caller sends HTML with
        // Content-Type: image/png. Trusting the header would store it, and serving it back
        // would execute it in a victim's browser.
        ContentTypeDetector.Detect("<html><script>"u8.ToArray()).Should().BeNull();
    }

    [Fact]
    public void Rejects_svg()
    {
        // SVG is an image format that runs script. It is excluded from the allowlist on
        // purpose, and it is also textual, so there is no magic number that could detect it.
        ContentTypeDetector.Detect("<svg xmlns="u8.ToArray()).Should().BeNull();
    }

    [Fact]
    public void Rejects_executables()
    {
        ContentTypeDetector.Detect(Header([0x4D, 0x5A])).Should().BeNull("MZ is a Windows executable");
        ContentTypeDetector.Detect(Header([0x7F, 0x45, 0x4C, 0x46])).Should().BeNull("ELF is a Linux executable");
        ContentTypeDetector.Detect(Header([0x50, 0x4B, 0x03, 0x04])).Should().BeNull("PK is a zip archive");
    }

    [Fact]
    public void Rejects_a_file_too_short_to_identify()
    {
        // Not an allow-by-default: a truncated upload must fail closed.
        ContentTypeDetector.Detect([0x89, 0x50]).Should().BeNull();
        ContentTypeDetector.Detect([]).Should().BeNull();
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/webp", ".webp")]
    public void Maps_content_type_to_extension(string contentType, string expected) =>
        ContentTypeDetector.ExtensionFor(contentType).Should().Be(expected);

    private static byte[] Header(byte[] magic)
    {
        var header = new byte[ContentTypeDetector.RequiredHeaderBytes];
        magic.CopyTo(header, 0);
        return header;
    }
}
