using System.ComponentModel.DataAnnotations;

namespace ShoppingList.Api.Configuration;

public sealed class KeycloakSettings
{
    public const string SectionName = "KeycloakSettings";

    /// <summary>
    /// The issuer exactly as it appears in the token's <c>iss</c> claim — the host-visible URL.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string Authority { get; init; } = string.Empty;

    /// <summary>
    /// Where the API fetches OIDC discovery and JWKS from: the internal container address.
    /// <para>
    /// This differing from <see cref="Authority"/> is the correct resolution of the Docker
    /// issuer mismatch. Keycloak stamps <c>iss</c> from its configured hostname, which must be
    /// the address clients see; but the API resolves metadata over the internal network where
    /// that hostname does not resolve. The common workaround — <c>ValidateIssuer = false</c> —
    /// disables the check that stops a token from another issuer being accepted, and is a real
    /// security regression rather than a configuration shortcut.
    /// </para>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string MetadataAddress { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string TokenEndpoint { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string AdminBaseUrl { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Realm { get; init; } = string.Empty;

    /// <summary>
    /// Expected <c>aud</c> claim. Requires an audience mapper on the Keycloak client — without
    /// one no usable audience is emitted at all.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string ClientId { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>False only for local HTTP development; the stack has no TLS termination.</summary>
    public bool RequireHttpsMetadata { get; init; } = true;

    /// <summary>
    /// Tightened from the framework default of 300 seconds. That default exists for clocks that
    /// drift; it also means a token stays accepted for five minutes past its stated expiry.
    /// </summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; } = 30;
}
