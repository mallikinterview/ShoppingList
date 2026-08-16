using ShoppingList.Api.Common.Pagination;

namespace ShoppingList.Tests.Unit;

public sealed class CursorTests
{
    [Fact]
    public void Round_trips_a_cursor()
    {
        var createdAt = new DateTimeOffset(2026, 8, 14, 10, 30, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();

        Cursor.TryDecode(Cursor.Encode(createdAt, id), out var decodedAt, out var decodedId).Should().BeTrue();

        decodedAt.Should().BeCloseTo(createdAt, TimeSpan.FromMilliseconds(1));
        decodedId.Should().Be(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")]
    [InlineData("../../etc/passwd")]
    public void Malformed_cursors_are_rejected_without_throwing(string? cursor)
    {
        // A cursor is untrusted client input. Throwing on a malformed one turns a mistyped URL
        // into a 500; returning false lets the endpoint answer 400 instead.
        Cursor.TryDecode(cursor, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Cursor_is_url_safe()
    {
        // Base64 '+' and '/' would be mangled in a query string, producing a cursor that
        // decodes differently than it was issued.
        var cursor = Cursor.Encode(DateTimeOffset.UtcNow, Guid.NewGuid());

        cursor.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }
}
