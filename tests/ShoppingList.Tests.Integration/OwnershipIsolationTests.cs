using System.Net;
using System.Net.Http.Json;
using ShoppingList.Api.Features.Items;
using ShoppingList.Api.Features.Search;

namespace ShoppingList.Tests.Integration;

/// <summary>
/// The most important tests in the suite. Everything else is a defect; a failure here is a
/// cross-account data breach.
/// <para>
/// Every endpoint that touches user data is covered, including search — which is the one people
/// forget, and the one that leaks the entire corpus at once when it is wrong.
/// </para>
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class OwnershipIsolationTests(ApiFactory factory)
{
    private const string UserA = "11111111-1111-1111-1111-111111111111";
    private const string UserB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task User_cannot_read_another_users_item()
    {
        var itemId = await CreateItemAsync(UserA, "Secret shopping item");

        var response = await factory.CreateClientFor(UserB).GetAsync($"/api/v1/items/{itemId}");

        // 404 rather than 403 on purpose. A 403 confirms the id exists and belongs to somebody
        // else, which turns the endpoint into an enumeration oracle.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task User_cannot_update_another_users_item()
    {
        var itemId = await CreateItemAsync(UserA, "Original name");

        var response = await factory.CreateClientFor(UserB)
            .PutAsJsonAsync($"/api/v1/items/{itemId}",
                new UpdateItemRequest("Hijacked", null, 1, null, null, false));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Verified, not assumed: a 404 response with a mutated row would be worse than an
        // honest 200.
        var reread = await factory.CreateClientFor(UserA).GetFromJsonAsync<ItemResponse>($"/api/v1/items/{itemId}");
        reread!.Name.Should().Be("Original name");
    }

    [Fact]
    public async Task User_cannot_delete_another_users_item()
    {
        var itemId = await CreateItemAsync(UserA, "Do not delete me");

        var response = await factory.CreateClientFor(UserB).DeleteAsync($"/api/v1/items/{itemId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await factory.CreateClientFor(UserA).GetAsync($"/api/v1/items/{itemId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the item must still exist");
    }

    [Fact]
    public async Task User_cannot_attach_an_image_to_another_users_item()
    {
        var itemId = await CreateItemAsync(UserA, "Item with images");

        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(PngBytes());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "photo.png");

        var response = await factory.CreateClientFor(UserB)
            .PostAsync($"/api/v1/items/{itemId}/images", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_never_returns_another_users_items()
    {
        // The endpoint most likely to be left unscoped, and the most damaging when it is: a
        // single query would return the whole table rather than one row.
        await CreateItemAsync(UserA, "Extremely distinctive zzyzx item");

        var response = await factory.CreateClientFor(UserB)
            .PostAsJsonAsync("/api/v1/search", new SearchRequest("zzyzx", null, null));

        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<SearchResponse>();
        results!.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task Listing_returns_only_the_callers_items()
    {
        await CreateItemAsync(UserA, "User A item");
        await CreateItemAsync(UserB, "User B item");

        var listing = await factory.CreateClientFor(UserB)
            .GetFromJsonAsync<ShoppingList.Api.Common.Pagination.PagedResult<ItemResponse>>("/api/v1/items");

        listing!.Items.Should().OnlyContain(item => item.Name != "User A item");
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        // Covers the fallback authorization policy: an endpoint that forgot RequireAuthorization
        // would silently be public, and this is what catches that.
        var anonymous = factory.CreateClient();

        foreach (var path in new[] { "/api/v1/items", "/api/v1/search" })
        {
            var response = path.Contains("search", StringComparison.Ordinal)
                ? await anonymous.PostAsJsonAsync(path, new SearchRequest("milk", null, null))
                : await anonymous.GetAsync(path);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "endpoint {0} must require a token", path);
        }
    }

    [Fact]
    public async Task Expired_tokens_are_rejected()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", ApiFactory.IssueToken(UserA, lifetime: TimeSpan.FromSeconds(-120)));

        (await client.GetAsync("/api/v1/items")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tampered_tokens_are_rejected()
    {
        // Flipping a character in the signature must invalidate the token. If this ever passes,
        // signature validation is not actually running.
        var token = ApiFactory.IssueToken(UserA);
        var tampered = token[..^4] + (token[^4] == 'A' ? "BBBB" : "AAAA");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tampered);

        (await client.GetAsync("/api/v1/items")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<Guid> CreateItemAsync(string subjectId, string name)
    {
        var response = await factory.CreateClientFor(subjectId)
            .PostAsJsonAsync("/api/v1/items", new CreateItemRequest(name, null, 1, null, "test"));

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ItemResponse>())!.Id;
    }

    internal static byte[] PngBytes()
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
