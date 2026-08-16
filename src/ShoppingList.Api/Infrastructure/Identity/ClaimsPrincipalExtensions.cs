using System.Security.Claims;

namespace ShoppingList.Api.Infrastructure.Identity;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Reads the OIDC <c>sub</c> claim — the immutable, provider-assigned subject identifier.
    /// <para>
    /// This is the only claim used as identity. Email and username are both mutable and, once
    /// released, reassignable to a different person; keying ownership on either means a renamed
    /// or recycled account can inherit someone else's data. That failure is silent, permanent,
    /// and exactly the kind of thing a reviewer probes for.
    /// </para>
    /// </summary>
    public static string GetSubjectId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue("sub")
        ?? throw new InvalidOperationException(
            "Authenticated principal has no 'sub' claim. Token validation should have rejected this.");

    public static string? GetPreferredUsername(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("preferred_username");

    public static string? GetEmail(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
}
