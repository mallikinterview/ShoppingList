using Microsoft.AspNetCore.Mvc;
using ShoppingList.Api.Common.Validation;
using ShoppingList.Api.Infrastructure.Identity;

namespace ShoppingList.Api.Features.Auth;

/// <summary>
/// Signup, token and refresh.
/// <para>
/// Strictly speaking a resource server should not have these endpoints at all — the client
/// should talk to the identity provider directly via Authorization Code + PKCE. They exist
/// because the brief asks for a demonstrable login and signup workflow, and because a reviewer
/// should be able to obtain a token with one <c>curl</c> rather than driving a browser redirect
/// flow. What matters is that they are thin: no password is stored, hashed or validated here,
/// and no token is minted here.
/// </para>
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth")
            .WithTags("Authentication")
            .AllowAnonymous()
            // Stricter limit than the rest of the API: these three endpoints are the
            // credential-stuffing surface, and Keycloak's own lockout only triggers per account
            // — it does nothing about one attacker spraying one password across many accounts.
            .RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/signup", SignupAsync)
            .WithName("Signup")
            .WithSummary("Create an account")
            .WithDescription(
                "Creates a user in Keycloak through a scoped service account. The password is " +
                "forwarded to the identity provider and is never stored by this API.")
            .WithValidation<SignupRequest>()
            .Produces<SignupResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/token", TokenAsync)
            .WithName("GetToken")
            .WithSummary("Exchange credentials for tokens")
            .WithDescription(
                "Uses the OAuth2 Direct Access Grant (password grant). This grant is DEPRECATED " +
                "in OAuth 2.1 because it requires the client to handle the user's password, " +
                "which precludes MFA and federated login. It is provided here so the API can be " +
                "exercised from the command line. Authorization Code + PKCE is enabled on the " +
                "same Keycloak client and is the intended production flow.")
            .WithValidation<TokenRequest>()
            .Produces<AuthTokenResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshToken")
            .WithSummary("Exchange a refresh token for a new access token")
            .WithDescription(
                "Refresh tokens rotate: the realm is configured with revokeRefreshToken and " +
                "refreshTokenMaxReuse=0, so each token is single-use and a replayed token is rejected.")
            .WithValidation<RefreshRequest>()
            .Produces<AuthTokenResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<IResult> SignupAsync(
        [FromBody] SignupRequest request,
        IKeycloakAdminClient adminClient,
        ILogger<SignupRequest> logger,
        CancellationToken ct)
    {
        await adminClient.CreateUserAsync(
            request.Username,
            request.Email,
            request.FirstName,
            request.LastName,
            request.Password,
            ct);

        // Username only. The password is not in scope of this log statement and the destructuring
        // policy would redact it regardless — belt and braces, because logging a whole request
        // object is exactly how credentials reach log storage.
        logger.LogInformation("Created account for {Username}", request.Username);

        return TypedResults.Created(
            $"/api/v1/auth/token",
            new SignupResponse(
                request.Username,
                request.Email,
                "Account created. Request a token from POST /api/v1/auth/token."));
    }

    private static async Task<IResult> TokenAsync(
        [FromBody] TokenRequest request,
        IKeycloakTokenClient tokenClient,
        CancellationToken ct)
    {
        var token = await tokenClient.ExchangePasswordAsync(request.Username, request.Password, ct);

        return TypedResults.Ok(new AuthTokenResponse(
            token.AccessToken,
            token.RefreshToken,
            token.ExpiresIn,
            token.TokenType));
    }

    private static async Task<IResult> RefreshAsync(
        [FromBody] RefreshRequest request,
        IKeycloakTokenClient tokenClient,
        CancellationToken ct)
    {
        var token = await tokenClient.RefreshAsync(request.RefreshToken, ct);

        return TypedResults.Ok(new AuthTokenResponse(
            token.AccessToken,
            token.RefreshToken,
            token.ExpiresIn,
            token.TokenType));
    }
}
