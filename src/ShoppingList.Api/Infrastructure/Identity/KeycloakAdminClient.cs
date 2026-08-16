using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ShoppingList.Api.Common.Errors;
using ShoppingList.Api.Configuration;

namespace ShoppingList.Api.Infrastructure.Identity;

public interface IKeycloakAdminClient
{
    Task CreateUserAsync(
        string username,
        string email,
        string firstName,
        string lastName,
        string password,
        CancellationToken ct);
}

/// <summary>
/// Creates users through Keycloak's Admin REST API using a service account.
/// <para>
/// This is what makes <c>POST /auth/signup</c> possible without the API storing a credential.
/// The password is forwarded to Keycloak and never persisted, hashed or logged here; password
/// policy, history and brute-force lockout are all enforced by the realm.
/// </para>
/// <para>
/// The service account holds <c>manage-users</c> and <c>view-users</c> and nothing else, so a
/// compromise of this code path cannot alter realm configuration, clients or role mappings.
/// </para>
/// </summary>
internal sealed class KeycloakAdminClient(
    HttpClient httpClient,
    IOptions<KeycloakSettings> options,
    ILogger<KeycloakAdminClient> logger) : IKeycloakAdminClient, IDisposable
{
    private readonly KeycloakSettings _settings = options.Value;

    // Cached because signup would otherwise perform two round trips to Keycloak per call. The
    // margin means a token is never presented within 30 seconds of its expiry, which removes a
    // whole class of intermittent failure under clock drift.
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public async Task CreateUserAsync(
        string username,
        string email,
        string firstName,
        string lastName,
        string password,
        CancellationToken ct)
    {
        var accessToken = await GetServiceAccountTokenAsync(ct);

        var payload = new
        {
            username,
            email,

            // Keycloak's declarative user profile — enabled by default since Keycloak 24 —
            // marks firstName and lastName as required attributes. A user created without them
            // is created successfully, and is then flagged with the VERIFY_PROFILE required
            // action; every password grant for that account afterwards fails with
            // "Account is not fully set up".
            //
            // The failure mode is the awkward part: signup returns 201 and login is impossible.
            // The two halves disagree, and only the first half is visible from the outside, so
            // the account looks created right up until someone tries to use it.
            //
            // Both are forwarded from the signup request rather than derived from the username.
            // Deriving them would have worked, and was the first fix attempted — but it stores
            // a name this API invented, which nobody asked for and nobody can correct. It also
            // lets the signup contract and the identity provider's requirements drift apart
            // silently, which is the shape of the original defect rather than a departure from
            // it. Collecting the fields keeps the two in agreement, and means the realm's
            // VERIFY_PROFILE action can stay enabled and do its job instead of being switched
            // off to accommodate an incomplete payload.
            firstName,
            lastName,

            enabled = true,
            emailVerified = true,
            credentials = new[]
            {
                new { type = "password", value = password, temporary = false }
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_settings.AdminBaseUrl.TrimEnd('/')}/admin/realms/{_settings.Realm}/users")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ConflictException("An account with that username or email already exists.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Realm password policy rejections land here. The realm's message is surfaced
            // because it tells the user what to fix; it contains no account information.
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogInformation("Keycloak rejected user creation: {Body}", body);

            throw new BadRequestException(ExtractErrorMessage(body)
                ?? "The account could not be created. Check that the password meets the required policy.");
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Keycloak admin API returned {StatusCode} creating a user", (int)response.StatusCode);
            throw new DependencyUnavailableException("keycloak");
        }
    }

    private async Task<string> GetServiceAccountTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _cachedTokenExpiry)
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Re-checked inside the lock: several requests can queue here while the first is
            // fetching, and without this they would each fetch again on release.
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _cachedTokenExpiry)
            {
                return _cachedToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _settings.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _settings.ClientId,
                    ["client_secret"] = _settings.ClientSecret
                })
            };

            using var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to obtain service-account token: {StatusCode}", (int)response.StatusCode);
                throw new DependencyUnavailableException("keycloak");
            }

            var token = await response.Content.ReadFromJsonAsync<ServiceAccountToken>(cancellationToken: ct)
                        ?? throw new DependencyUnavailableException("keycloak");

            _cachedToken = token.AccessToken;
            _cachedTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(token.ExpiresIn - 30, 5));

            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("errorMessage", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _tokenLock.Dispose();

    private sealed record ServiceAccountToken(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
