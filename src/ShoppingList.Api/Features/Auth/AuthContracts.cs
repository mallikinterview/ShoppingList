using FluentValidation;
using ShoppingList.Api.Common.Extensions;

namespace ShoppingList.Api.Features.Auth;

public sealed record SignupRequest(
    [property: OpenApiExample("reviewer")] string Username,
    [property: OpenApiExample("reviewer@example.com")] string Email,
    [property: OpenApiExample("Alex")] string FirstName,
    [property: OpenApiExample("Reviewer")] string LastName,
    // Satisfies the realm's own policy — length(10) and notUsername — so the published example
    // works as submitted. An example this API's validator accepts but Keycloak rejects would
    // reproduce, in the documentation, exactly the disagreement the FirstName/LastName comment
    // below describes.
    [property: OpenApiExample("Str0ng!Passphrase")] string Password);

public sealed record TokenRequest(
    [property: OpenApiExample("reviewer")] string Username,
    [property: OpenApiExample("Str0ng!Passphrase")] string Password);

public sealed record RefreshRequest(
    [property: OpenApiExample("paste the refreshToken from POST /api/v1/auth/token")]
    string RefreshToken);

public sealed record AuthTokenResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    string TokenType);

public sealed record SignupResponse(string Username, string Email, string Message);

internal sealed class SignupRequestValidator : AbstractValidator<SignupRequest>
{
    public SignupRequestValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty()
            .Length(3, 64)
            // Restricted rather than merely non-empty: the username is echoed in logs and is
            // used to build the Keycloak admin request path.
            .Matches("^[a-zA-Z0-9._-]+$")
            .WithMessage("Username may contain only letters, digits, dots, underscores and hyphens.");

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(254);

        // Required because Keycloak's declarative user profile — the default since v24 —
        // marks both as required attributes. An account created without them is created
        // successfully and then flagged VERIFY_PROFILE, so signup returns 201 and every
        // password grant afterwards fails with "Account is not fully set up": the two halves
        // disagree and only the first is visible from outside.
        //
        // Collected here rather than synthesised from the username, because a name this API
        // invented is data nobody asked for and nobody can correct. Bounded but otherwise
        // unrestricted: names legitimately contain apostrophes, hyphens, spaces and accented
        // characters, and a character allowlist here would reject real people.
        RuleFor(x => x.FirstName)
            .NotEmpty()
            .MaximumLength(64);

        RuleFor(x => x.LastName)
            .NotEmpty()
            .MaximumLength(64);

        // Length only. The realm owns password policy — complexity, history, reuse — and
        // duplicating those rules here would guarantee they eventually disagree, producing
        // passwords this API accepts and Keycloak rejects.
        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(10)
            .WithMessage("Password must be at least 10 characters.")
            .MaximumLength(128);
    }
}

internal sealed class TokenRequestValidator : AbstractValidator<TokenRequest>
{
    public TokenRequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

internal sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(8192);
    }
}
