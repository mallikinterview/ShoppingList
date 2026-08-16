using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ShoppingList.Api.Common.Errors;
using ShoppingList.Api.Configuration;

namespace ShoppingList.Api.Infrastructure.Identity;

public interface IKeycloakTokenClient
{
    Task<TokenResponse> ExchangePasswordAsync(string username, string password, CancellationToken ct);

    Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct);
}

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_expires_in")] int RefreshExpiresIn,
    [property: JsonPropertyName("token_type")] string TokenType);

/// <summary>
/// Thin pass-through to Keycloak's token endpoint.
/// <para>
/// This API never mints a token. It forwards credentials to the identity provider and returns
/// what comes back, so there is no signing key, no password hash and no token lifetime policy in
/// this codebase to get wrong or to defend in review.
/// </para>
/// <para>
/// The password grant (RFC 6749 §4.3, "Direct Access Grant") is used here and is <b>deprecated in
/// OAuth 2.1</b>: it requires the client to handle the user's password, which rules out MFA,
/// federated login and any consent step. It exists in this project so the API can be exercised
/// with one <c>curl</c> command. Authorization Code + PKCE is enabled on the same Keycloak client
/// and is the intended production path.
/// </para>
/// </summary>
internal sealed class KeycloakTokenClient(
    HttpClient httpClient,
    IOptions<KeycloakSettings> options,
    ILogger<KeycloakTokenClient> logger) : IKeycloakTokenClient
{
    private readonly KeycloakSettings _settings = options.Value;

    public Task<TokenResponse> ExchangePasswordAsync(string username, string password, CancellationToken ct) =>
        RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
            ["username"] = username,
            ["password"] = password,
            ["scope"] = "openid profile email"
        }, "password", ct);

    public Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct) =>
        RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
            ["refresh_token"] = refreshToken
        }, "refresh_token", ct);

    private async Task<TokenResponse> RequestTokenAsync(
        Dictionary<string, string> form,
        string grantType,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await SendAsync(request, grantType, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            // Nothing from Keycloak's error body is echoed to the caller. Its responses
            // distinguish "no such user" from "wrong password" and from "account disabled",
            // which together make a usable account-enumeration oracle. The client gets one
            // undifferentiated failure; the detail goes to the log.
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Keycloak rejected {GrantType} grant with {StatusCode}: {Body}",
                grantType, (int)response.StatusCode, body);

            throw grantType == "refresh_token"
                ? new BadRequestException("The refresh token is invalid or has expired.")
                : new BadRequestException("Invalid username or password.");
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Keycloak token endpoint returned {StatusCode} for {GrantType}",
                (int)response.StatusCode, grantType);
            throw new DependencyUnavailableException("keycloak");
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);

        return payload ?? throw new DependencyUnavailableException("keycloak");
    }

    /// <summary>
    /// Sends the request and translates a failure to <i>reach</i> Keycloak into
    /// <see cref="DependencyUnavailableException"/>.
    /// <para>
    /// Without this the two failure modes are not distinguished. Keycloak answering with an
    /// unexpected status is already mapped to 503 by the caller, but Keycloak not answering at
    /// all throws out of <c>SendAsync</c> before there is any status to inspect — and an
    /// unrecognised exception becomes 500. So a restarting identity provider was reported as
    /// "we have a bug" rather than "a dependency is down, retry", which is wrong for the client
    /// (a 500 is not worth retrying, a 503 is), wrong for the on-call engineer, and wrong for
    /// any alert that distinguishes the two. It also made the existing 503 mapping look like it
    /// covered this and it did not.
    /// </para>
    /// <para>
    /// The resilience pipeline's own rejections are caught here too, and that is the subtle part.
    /// Bounding the attempt timeout at two seconds put it in a race with the connection failure:
    /// on a host where a refused connection surfaces in slightly over two seconds, the timeout
    /// wins and Polly throws <c>TimeoutRejectedException</c> instead of the socket error. Same
    /// cause, same remedy, different exception — and therefore, if only the socket error were
    /// handled, a different status code depending on which lost by a few milliseconds. A status
    /// code that varies with host timing is not a contract. Both rejections mean the same thing
    /// from this endpoint's perspective: Keycloak did not serve the request, so 503 either way.
    /// </para>
    /// <para>
    /// The generic <c>TimeoutRejectedException → 504</c> mapping in the global handler stays as a
    /// backstop for outbound calls that have no client of their own to name the dependency.
    /// </para>
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string grantType,
        CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            // Connection refused, DNS failure, TLS failure. Keycloak never responded.
            logger.LogError(ex, "Keycloak was unreachable for a {GrantType} grant.", grantType);
            throw new DependencyUnavailableException("keycloak", ex);
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or BrokenCircuitException)
        {
            // The resilience pipeline gave up: either the attempt exceeded its timeout, or the
            // circuit is open and the call was never made. Both are "Keycloak is not serving
            // requests right now", which is the same answer the caller needs.
            logger.LogError(ex, "The resilience pipeline rejected a {GrantType} grant to Keycloak.",
                grantType);
            throw new DependencyUnavailableException("keycloak", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The HttpClient's own timeout, not the caller hanging up. The guard on the token is
            // what separates them: a caller who disconnects is a 499 and is not a failure of
            // ours, whereas Keycloak exceeding the configured timeout is a dependency outage.
            logger.LogError(ex, "Keycloak timed out for a {GrantType} grant.", grantType);
            throw new DependencyUnavailableException("keycloak", ex);
        }
    }
}
