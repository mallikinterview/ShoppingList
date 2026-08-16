using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Pgvector;
using ShoppingList.Api.Data;
using ShoppingList.Api.Infrastructure.Embeddings;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ShoppingList.Tests.Integration;

/// <summary>
/// Boots the real application against real Postgres, Redis and Minio containers.
/// <para>
/// Two deliberate substitutions, both of which are performance decisions rather than shortcuts:
/// </para>
/// <list type="bullet">
/// <item><b>Tokens are signed in-process</b> with a test RSA key instead of by a Keycloak
/// container. Keycloak takes 30-60 seconds to become ready — it would dominate the suite's
/// runtime, and it would be testing Keycloak rather than this application. The token validation
/// path exercised here is byte-for-byte the production one: real RS256 signatures, real issuer
/// and audience checks, real expiry. A separate test covers the genuine Keycloak flow.</item>
/// <item><b>Embeddings are deterministic</b> rather than produced by a live model. Ranking
/// assertions need to be reproducible, and a real model would make "does this rank above that"
/// a question about model weights rather than about the fusion logic under test.</item>
/// </list>
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    internal const string TestIssuer = "https://test-issuer.local/realms/shopping-list";
    internal const string TestAudience = "shopping-list-api";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        // pgvector, not the stock postgres image — the extension has to be compiled in.
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("shoppinglist_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7.4-alpine")
        .Build();

    private readonly MinioContainer _minio = new MinioBuilder()
        .WithImage("minio/minio:RELEASE.2025-04-22T22-12-26Z")
        .Build();

    private readonly List<KeyValuePair<string, string?>> _replacedVariables = [];

    public StubEmbeddingGenerator Embeddings { get; } = new();

    // Explicit interface implementations: xunit v2's IAsyncLifetime returns Task, while
    // WebApplicationFactory's own DisposeAsync returns ValueTask. Implementing the interface
    // explicitly keeps both signatures valid instead of forcing one to shadow the other.
    async Task IAsyncLifetime.InitializeAsync()
    {
        // Started concurrently: sequentially these add up to most of the suite's startup time.
        await Task.WhenAll(
            _postgres.StartAsync(),
            _redis.StartAsync(),
            _minio.StartAsync());

        // Must happen before anything touches Services, because that is what builds the host.
        ApplyTestConfiguration();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Schema comes from the real migrations, so the tests exercise the same DDL that
        // production runs — including the generated tsvector column and both indexes.
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        RestoreEnvironment();

        // Disposed concurrently for the same reason they are started concurrently — three
        // sequential container teardowns are pure wall-clock cost at the end of every run.
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask(),
            _minio.DisposeAsync().AsTask());
    }

    /// <summary>
    /// Test configuration, supplied as environment variables rather than through
    /// <c>ConfigureAppConfiguration</c>. The reason is worth recording, because the failure mode
    /// is opaque.
    /// <para>
    /// <c>Program.cs</c> uses top-level statements, so <see cref="WebApplicationFactory{T}"/> has
    /// no host builder to call: it runs the entry point and intercepts the host through
    /// <c>HostFactoryResolver</c>. Configuration callbacks registered on the factory are therefore
    /// applied to the <c>WebApplicationBuilder</c> only when <c>Build()</c> is reached — but the
    /// composition root reads several values straight off <c>builder.Configuration</c> while it is
    /// still registering services, the Npgsql data source's connection string among them. Those
    /// reads run first and see nothing, and the host dies on a null connection string long before
    /// the in-memory values are ever added.
    /// </para>
    /// <para>
    /// Environment variables remove the ordering question entirely: <c>CreateBuilder</c> reads
    /// them before the first line of registration code executes. They also mean the test host is
    /// configured through exactly the same <c>Section__Key</c> surface the container is, so this
    /// suite would catch a binding mistake that only shows up under that convention.
    /// </para>
    /// <para>
    /// The environment is process-wide state, which is only safe here because every test class in
    /// this assembly shares one <c>ApiCollection</c> fixture — one factory, one host, no other
    /// collection running alongside it. Previous values are captured and restored on dispose so
    /// nothing leaks into a subsequent run in the same process.
    /// </para>
    /// </summary>
    private void ApplyTestConfiguration()
    {
        var minioEndpoint = _minio.GetConnectionString().Replace("http://", string.Empty, StringComparison.Ordinal);

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DatabaseSettings__ConnectionString"] = _postgres.GetConnectionString(),

            ["RedisSettings__ConnectionString"] = _redis.GetConnectionString(),
            ["RedisSettings__AbortOnConnectFail"] = "false",
            ["RedisSettings__InstanceName"] = "test",
            // Short TTL so invalidation tests do not have to wait out a five-minute expiry
            // — and zero jitter so their timing is deterministic.
            ["RedisSettings__DefaultTtlSeconds"] = "60",
            ["RedisSettings__TtlJitterSeconds"] = "0",

            ["MinioSettings__Endpoint"] = minioEndpoint,
            ["MinioSettings__PublicEndpoint"] = minioEndpoint,
            ["MinioSettings__AccessKey"] = MinioBuilder.DefaultUsername,
            ["MinioSettings__SecretKey"] = MinioBuilder.DefaultPassword,
            ["MinioSettings__BucketName"] = "test-images",
            ["MinioSettings__UseSsl"] = "false",
            ["MinioSettings__MaxUploadBytes"] = "1048576",

            ["KeycloakSettings__Authority"] = TestIssuer,
            ["KeycloakSettings__MetadataAddress"] = $"{TestIssuer}/.well-known/openid-configuration",
            // Port 1 is privileged and never listening, so a connection here is refused
            // immediately and deterministically on every platform. That is exactly the state
            // this host is in — there is no Keycloak container by design — and it makes the
            // identity-provider-is-down path directly testable rather than hypothetical.
            // A non-resolving hostname would do the same job but leaves the timing at the mercy
            // of whatever the machine's DNS does with an unknown name.
            ["KeycloakSettings__TokenEndpoint"] = "http://127.0.0.1:1/realms/shopping-list/protocol/openid-connect/token",
            ["KeycloakSettings__AdminBaseUrl"] = "https://test-issuer.local",
            ["KeycloakSettings__Realm"] = "shopping-list",
            ["KeycloakSettings__Audience"] = TestAudience,
            ["KeycloakSettings__ClientId"] = "shopping-list-api",
            ["KeycloakSettings__ClientSecret"] = "test-secret",
            ["KeycloakSettings__RequireHttpsMetadata"] = "false",

            ["OllamaSettings__BaseUrl"] = "http://localhost:11434",
            ["OllamaSettings__EmbeddingDimensions"] = "768",

            ["SearchSettings__Strategy"] = "rrf",
            ["SearchSettings__Experiment__Enabled"] = "true",
            ["SearchSettings__Experiment__VariantSplit"] = "50",

            // Effectively disabled: a limit that trips mid-suite would make unrelated tests
            // fail depending on execution order.
            ["RateLimitSettings__PermitLimit"] = "10000",
            ["RateLimitSettings__AuthPermitLimit"] = "10000",
            ["RateLimitSettings__UploadPermitLimit"] = "10000",

            ["SerilogSettings__LokiUrl"] = "http://localhost:3100",
            ["SerilogSettings__MinimumLevel"] = "Warning"
        };

        foreach (var (key, value) in settings)
        {
            _replacedVariables.Add(new KeyValuePair<string, string?>(key, Environment.GetEnvironmentVariable(key)));
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, value) in _replacedVariables)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _replacedVariables.Clear();
    }

    /// <summary>
    /// Guards the one ordering mistake this design can still make. Touching
    /// <see cref="WebApplicationFactory{T}.Services"/> before <c>InitializeAsync</c> has run would
    /// build the host with no container endpoints configured, and the resulting
    /// <c>ArgumentNullException</c> names a parameter rather than the cause. Saying so directly
    /// costs three lines.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (_replacedVariables.Count == 0)
        {
            throw new InvalidOperationException(
                "The test host was built before the containers were started. Resolve ApiFactory " +
                "through the ApiCollection fixture so xunit runs InitializeAsync first; " +
                "constructing it directly inside a test skips container startup entirely.");
        }

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Deterministic embeddings — see the class remarks.
            services.RemoveAll<IEmbeddingGenerator>();
            services.AddSingleton<IEmbeddingGenerator>(Embeddings);

            // Supplying Configuration directly stops the handler from fetching discovery over
            // the network. Everything else about validation stays exactly as configured in
            // production code.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MetadataAddress = null!;
                options.Authority = null;
                options.RequireHttpsMetadata = false;

                var configuration = new OpenIdConnectConfiguration { Issuer = TestIssuer };
                configuration.SigningKeys.Add(SigningKey);
                options.Configuration = configuration;

                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                options.TokenValidationParameters.ValidIssuer = TestIssuer;
                options.TokenValidationParameters.ValidAudience = TestAudience;
            });
        });
    }

    /// <summary>Issues a genuine RS256 token for the given subject.</summary>
    public HttpClient CreateClientFor(string subjectId, string? username = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", IssueToken(subjectId, username));
        return client;
    }

    internal static string IssueToken(string subjectId, string? username = null, TimeSpan? lifetime = null)
    {
        var handler = new JwtSecurityTokenHandler();

        var now = DateTime.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromMinutes(30));

        // A negative lifetime is a legitimate request — it is how the expired-token test gets its
        // subject. The validity window therefore has to be anchored to the expiry rather than to
        // now: anchoring it to now puts nbf after exp on an already-expired token, and the handler
        // refuses to construct it at all. The test would then fail inside itself rather than at
        // the API boundary it is supposed to be exercising, which is a false negative dressed up
        // as a real one.
        var notBefore = (expires < now ? expires : now).AddMinutes(-1);

        var token = handler.CreateJwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            subject: new ClaimsIdentity(
            [
                new Claim("sub", subjectId),
                new Claim("preferred_username", username ?? subjectId),
                new Claim("email", $"{username ?? subjectId}@example.test")
            ]),
            notBefore: notBefore,
            expires: expires,
            issuedAt: notBefore,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));

        return handler.WriteToken(token);
    }
}

/// <summary>
/// Deterministic stand-in for the embedding model.
/// <para>
/// Produces a stable vector from the text's own bytes, so the same text always embeds to the
/// same point and similar text lands nearby. That is enough to exercise the vector branch, the
/// HNSW index and the fusion arithmetic, while keeping ranking assertions reproducible.
/// </para>
/// <para><see cref="IsAvailable"/> makes the degradation path directly testable: flip it to
/// false and the search must fall back to keyword-only rather than failing.</para>
/// </summary>
public sealed class StubEmbeddingGenerator : IEmbeddingGenerator
{
    public bool IsAvailable { get; set; } = true;

    public string ModelName => "stub-embed";

    public int Dimensions => 768;

    public Task<Vector?> GenerateAsync(string text, CancellationToken ct)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult<Vector?>(null);
        }

        var values = new float[Dimensions];
        var bytes = System.Text.Encoding.UTF8.GetBytes(text.Trim().ToLowerInvariant());

        for (var i = 0; i < bytes.Length && i < Dimensions; i++)
        {
            values[i % Dimensions] += bytes[i] / 255f;
        }

        // Normalised to unit length: cosine distance is only meaningful on direction, and an
        // un-normalised vector would make magnitude leak into the ranking.
        var magnitude = MathF.Sqrt(values.Sum(v => v * v));
        if (magnitude > 0)
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] /= magnitude;
            }
        }
        else
        {
            values[0] = 1f;
        }

        return Task.FromResult<Vector?>(new Vector(values));
    }
}
