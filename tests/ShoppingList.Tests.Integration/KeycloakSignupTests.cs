using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShoppingList.Api.Common.Errors;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Infrastructure.Identity;
using Testcontainers.Keycloak;

namespace ShoppingList.Tests.Integration;

/// <summary>
/// Holds the Keycloak container for the whole class.
/// <para>
/// xunit constructs a new test-class instance per test method, so a container owned by the
/// test class itself is started and torn down once per <c>[Fact]</c> — four times here, at
/// roughly 25 seconds each. A class fixture is created once and shared, which is the
/// difference between a suite that costs half a minute and one that costs two.
/// </para>
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime, IDisposable
{
    internal const string Realm = "shopping-list";
    internal const string ClientId = "shopping-list-api";
    internal const string ClientSecret = "local-dev-client-secret-change-me";

    // Pinned to the same image docker-compose.yml runs. A test that passes against a different
    // Keycloak major than the one shipped would be reassuring about the wrong thing — this
    // defect was introduced by a default that changed between versions.
    private readonly KeycloakContainer _keycloak = new KeycloakBuilder()
        .WithImage("quay.io/keycloak/keycloak:26.0")
        .WithResourceMapping(new FileInfo(RealmFile()), "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .Build();

    private readonly HttpClient _httpClient = new();

    // Concrete types rather than the interfaces: nothing here is substituted, and naming the
    // implementation is what makes it obvious that the production classes are under test
    // rather than a paraphrase of them.
    //
    // internal, not public: both clients are internal to the API assembly and visible here
    // only through InternalsVisibleTo, so a public property could not expose them.
    internal KeycloakAdminClient AdminClient { get; private set; } = null!;

    internal KeycloakTokenClient TokenClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _keycloak.StartAsync();

        var baseAddress = _keycloak.GetBaseAddress().TrimEnd('/');

        var settings = Options.Create(new KeycloakSettings
        {
            Authority = $"{baseAddress}/realms/{Realm}",
            MetadataAddress = $"{baseAddress}/realms/{Realm}/.well-known/openid-configuration",
            TokenEndpoint = $"{baseAddress}/realms/{Realm}/protocol/openid-connect/token",
            AdminBaseUrl = baseAddress,
            Realm = Realm,
            Audience = ClientId,
            ClientId = ClientId,
            ClientSecret = ClientSecret,
            RequireHttpsMetadata = false
        });

        // Every URL these clients build is absolute, so a bare HttpClient with no BaseAddress
        // is all they need.
        AdminClient = new KeycloakAdminClient(_httpClient, settings, NullLogger<KeycloakAdminClient>.Instance);
        TokenClient = new KeycloakTokenClient(_httpClient, settings, NullLogger<KeycloakTokenClient>.Instance);
    }

    public async Task DisposeAsync() => await _keycloak.DisposeAsync();

    /// <summary>
    /// Separate from <see cref="DisposeAsync"/> because the container teardown is asynchronous
    /// and these two are not. xunit calls both.
    /// </summary>
    public void Dispose()
    {
        AdminClient?.Dispose();
        _httpClient.Dispose();
    }

    /// <summary>
    /// Walks up from the test binaries to find the realm the compose stack imports. Copying the
    /// file into the test project would let the two drift, and the whole point of this suite is
    /// that it runs against the configuration actually shipped.
    /// </summary>
    private static string RealmFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docker", "keycloak", "shopping-list-realm.json");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "docker/keycloak/shopping-list-realm.json was not found above the test output directory. " +
            "This suite imports the same realm the compose stack does, so it cannot run without it.");
    }
}

/// <summary>
/// The one suite that runs against a real Keycloak.
/// <para>
/// <see cref="ApiFactory"/> deliberately signs tokens in-process: a Keycloak container takes
/// the better part of a minute to become ready, and paying that on every test would be testing
/// Keycloak rather than this application. The cost of that decision is a seam — signup writes
/// to an identity provider that the rest of the suite never speaks to, so nothing there can
/// observe whether an account this API creates is actually usable.
/// </para>
/// <para>
/// That seam produced a real defect. Keycloak's declarative user profile, on by default since
/// v24, marks firstName and lastName required. Accounts created without them were created
/// successfully and then flagged VERIFY_PROFILE, and every password grant afterwards failed
/// with "Account is not fully set up". Signup returned 201; login was impossible. Every test
/// passed, because signup was only ever asserted as far as its status code and every token in
/// the suite was minted locally.
/// </para>
/// <para>
/// Because the realm keeps VERIFY_PROFILE enabled — the fix is the signup contract collecting
/// the names, not the realm being told to stop asking for them — this suite genuinely guards
/// the code path. Remove firstName and lastName from the admin client's payload and
/// <c>An_account_created_through_signup_can_immediately_obtain_a_token</c> fails, because
/// nothing else covers for it. An earlier version disabled the required action as well, and
/// the redundancy made the test unable to fail at all: two fixes for one defect meant neither
/// was under test.
/// </para>
/// </summary>
public sealed class KeycloakSignupTests(KeycloakFixture fixture) : IClassFixture<KeycloakFixture>
{
    private KeycloakAdminClient AdminClient => fixture.AdminClient;

    private KeycloakTokenClient TokenClient => fixture.TokenClient;

    [Fact]
    public async Task An_account_created_through_signup_can_immediately_obtain_a_token()
    {
        // The assertion the previous suite could not make. Signup returning 201 says the row
        // was written; it says nothing about whether the account works, and for a while it
        // did not.
        var username = NewUsername();
        const string password = "Passw0rd!2026";

        await AdminClient.CreateUserAsync(
            username, $"{username}@example.test", "Test", "User", password, CancellationToken.None);

        var token = await TokenClient.ExchangePasswordAsync(username, password, CancellationToken.None);

        token.AccessToken.Should().NotBeNullOrWhiteSpace(
            "an account this API creates must be usable with the credentials it was given");
        token.RefreshToken.Should().NotBeNullOrWhiteSpace();
        token.TokenType.Should().Be("Bearer");
        token.ExpiresIn.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_same_username_cannot_be_registered_twice()
    {
        var username = NewUsername();

        await AdminClient.CreateUserAsync(
            username, $"{username}@example.test", "Test", "User", "Passw0rd!2026", CancellationToken.None);

        var act = () => AdminClient.CreateUserAsync(
            username, $"{username}@example.test", "Test", "User", "Passw0rd!2026", CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task A_wrong_password_is_rejected_without_revealing_whether_the_account_exists()
    {
        var username = NewUsername();

        await AdminClient.CreateUserAsync(
            username, $"{username}@example.test", "Test", "User", "Passw0rd!2026", CancellationToken.None);

        var wrongPassword = await Record.ExceptionAsync(() =>
            TokenClient.ExchangePasswordAsync(username, "NotThePassword!1", CancellationToken.None));

        var noSuchUser = await Record.ExceptionAsync(() =>
            TokenClient.ExchangePasswordAsync(NewUsername(), "NotThePassword!1", CancellationToken.None));

        // Both are BadRequestException carrying the identical message. Keycloak distinguishes
        // "no such user" from "wrong password" in its own response body; echoing that
        // distinction would turn this endpoint into an account-enumeration oracle.
        wrongPassword.Should().BeOfType<BadRequestException>();
        noSuchUser.Should().BeOfType<BadRequestException>();
        wrongPassword!.Message.Should().Be(noSuchUser!.Message);
    }

    [Fact]
    public async Task A_refresh_token_cannot_be_used_twice()
    {
        var username = NewUsername();
        const string password = "Passw0rd!2026";

        await AdminClient.CreateUserAsync(
            username, $"{username}@example.test", "Test", "User", password, CancellationToken.None);

        var first = await TokenClient.ExchangePasswordAsync(username, password, CancellationToken.None);

        var refreshed = await TokenClient.RefreshAsync(first.RefreshToken, CancellationToken.None);
        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace();

        // The realm sets revokeRefreshToken with refreshTokenMaxReuse 0, so redeeming a token
        // burns it. A replayed token must fail — otherwise a stolen copy stays valid for the
        // whole refresh lifetime.
        var replay = () => TokenClient.RefreshAsync(first.RefreshToken, CancellationToken.None);

        await replay.Should().ThrowAsync<BadRequestException>();
    }

    /// <summary>
    /// Unique per test so the four cases never collide, and short enough to stay inside
    /// Keycloak's username constraints.
    /// </summary>
    private static string NewUsername() => $"signup{Guid.NewGuid():N}"[..20];
}
