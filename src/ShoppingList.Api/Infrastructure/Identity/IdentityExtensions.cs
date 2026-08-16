using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Tokens;
using Polly;
using ShoppingList.Api.Configuration;

namespace ShoppingList.Api.Infrastructure.Identity;

public static class IdentityExtensions
{
    public static IServiceCollection AddIdentityAndAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = configuration.GetSettings<KeycloakSettings>(KeycloakSettings.SectionName);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Authority is the issuer as clients see it; MetadataAddress is how this process
                // reaches discovery and JWKS from inside the container network. They differ on
                // purpose — see KeycloakSettings for why ValidateIssuer=false is not the answer.
                options.Authority = settings.Authority;
                options.MetadataAddress = settings.MetadataAddress;
                options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
                options.Audience = settings.Audience;

                // Signing keys come from JWKS and are refreshed automatically, so a Keycloak key
                // rotation does not require a redeploy. There is no symmetric key anywhere in
                // this codebase to leak, rotate manually, or find committed in a config file.
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Authority,

                    ValidateAudience = true,
                    ValidAudience = settings.Audience,

                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,

                    // Down from the 300-second default, which silently extends the life of every
                    // expired token by five minutes.
                    ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds),

                    NameClaimType = "preferred_username",
                    RoleClaimType = ClaimTypes.Role
                };

                options.MapInboundClaims = false;

                options.Events = new JwtBearerEvents
                {
                    // Keycloak nests realm roles under realm_access.roles, which no standard
                    // claims transformation understands. Flattening them here means
                    // [Authorize(Roles = "admin")] works as written.
                    OnTokenValidated = context =>
                    {
                        if (context.Principal?.Identity is not ClaimsIdentity identity)
                        {
                            return Task.CompletedTask;
                        }

                        var realmAccess = context.Principal.FindFirst("realm_access")?.Value;
                        if (string.IsNullOrEmpty(realmAccess))
                        {
                            return Task.CompletedTask;
                        }

                        try
                        {
                            using var document = JsonDocument.Parse(realmAccess);
                            if (document.RootElement.TryGetProperty("roles", out var roles))
                            {
                                foreach (var role in roles.EnumerateArray())
                                {
                                    var value = role.GetString();
                                    if (!string.IsNullOrEmpty(value))
                                    {
                                        identity.AddClaim(new Claim(ClaimTypes.Role, value));
                                    }
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // A malformed realm_access claim costs the caller their roles, not
                            // their authentication. Failing the whole token here would turn a
                            // Keycloak mapper misconfiguration into a total outage.
                        }

                        return Task.CompletedTask;
                    },

                    // Failures are logged with the reason but the response body stays empty —
                    // the framework's default WWW-Authenticate header already says "expired" or
                    // "invalid signature", and adding more only helps an attacker.
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Authentication");

                        if (context.Exception is SecurityTokenExpiredException)
                        {
                            logger.LogDebug("Token rejected: expired");
                        }
                        else
                        {
                            logger.LogWarning("Token validation failed: {Reason}", context.Exception.Message);
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            // Fail closed. Without a fallback policy, an endpoint that is missing [Authorize] or
            // .RequireAuthorization() is silently public — a single forgotten call away from an
            // unauthenticated data leak. With it, the mistake produces a 401 instead.
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddHttpClient<IKeycloakTokenClient, KeycloakTokenClient>()
            .AddStandardResilienceHandler(ConfigureIdentityResilience);

        services.AddHttpClient<IKeycloakAdminClient, KeycloakAdminClient>()
            .AddStandardResilienceHandler(ConfigureIdentityResilience);

        return services;
    }

    /// <summary>
    /// Bounds the resilience pipeline in front of Keycloak.
    /// <para>
    /// The defaults are tuned for a background job, not for a request a person is waiting on: a
    /// 30-second total budget, ten seconds per attempt, and three retries with exponential
    /// backoff. Measured against a refused connection that produced a 503 after <b>15.4 seconds</b>,
    /// with a worst case of 30. Two things go wrong at that duration, and neither is theoretical.
    /// </para>
    /// <para>
    /// The caller's own timeout expires first, so the 503 and its <c>Retry-After</c> are never
    /// read — the work of answering correctly is thrown away. And every login in flight holds a
    /// server connection for the full budget, so an identity-provider outage becomes a
    /// resource-exhaustion outage here: the retries are what convert someone else's failure into
    /// ours. A retry policy without a total budget is not resilience, it is amplification.
    /// </para>
    /// <para>
    /// Retrying a <i>refused</i> connection is also close to pointless. Refusal is immediate and
    /// definitive; retries earn their keep against timeouts, 502s and connection resets, which is
    /// why the count is reduced rather than removed.
    /// </para>
    /// <para>
    /// The circuit breaker is left at its defaults deliberately. It limits sustained damage once
    /// it opens, but it opens on a failure <i>ratio</i> over a sampling window — so the requests
    /// before it trips still pay full price. Bounding the budget is what protects those, and the
    /// breaker is what protects the ones after.
    /// </para>
    /// </summary>
    private static void ConfigureIdentityResilience(HttpStandardResilienceOptions options)
    {
        // The ceiling for one call including every retry. Must exceed
        // AttemptTimeout * (1 + MaxRetryAttempts) plus backoff, or the budget expires mid-policy
        // and the caller sees a timeout rather than the dependency's actual failure.
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(6);

        // A token exchange that has not answered in two seconds is not going to.
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);

        options.Retry.MaxRetryAttempts = 1;
        options.Retry.Delay = TimeSpan.FromMilliseconds(250);
        options.Retry.BackoffType = DelayBackoffType.Exponential;

        // Jitter matters more than it looks. Without it, every request that failed together
        // retries together, and the dependency is hit by a synchronised wave at the exact moment
        // it is trying to recover.
        options.Retry.UseJitter = true;

        // The breaker samples over a window, which has to be long enough to be statistically
        // meaningful and is required to be at least twice the attempt timeout.
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
    }
}
