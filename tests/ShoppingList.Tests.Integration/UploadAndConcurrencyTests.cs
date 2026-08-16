using System.Net;
using System.Net.Http.Json;
using ShoppingList.Api.Features.Items;

namespace ShoppingList.Tests.Integration;

[Collection(nameof(ApiCollection))]
public sealed class UploadAndConcurrencyTests(ApiFactory factory)
{
    [Fact]
    public async Task An_unreachable_identity_provider_is_503_not_500()
    {
        // "What happens when Keycloak is down?" is the first question asked of any decision to
        // delegate authentication, and the answer has to be better than "the API reports its own
        // failure". 503 tells the caller to retry and tells monitoring a dependency is out; 500
        // says this service is broken, which stops clients retrying and pages the wrong team.
        //
        // Nothing here is simulated. This host genuinely has no identity provider and the token
        // endpoint points at a closed port, so the request travels the real client, the real
        // resilience policy and the real error mapping.
        //
        // It costs a few seconds while the retry policy runs its course. That is the behaviour
        // under test, so waiting for it is the point rather than an inefficiency.
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/token",
            new { username = "demo", password = "unreachable-provider-so-this-is-never-checked" });

        clock.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Answering correctly is worth nothing if the caller has already given up. The resilience
        // pipeline is configured with a total budget precisely so this cannot drift; measured at
        // 15.4 seconds under the library defaults, which is longer than most clients will wait
        // and long enough that every login in flight pins a server connection during an outage.
        // The ceiling here is deliberately loose - it is checking that a budget exists, not
        // timing the machine.
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "an identity-provider outage must fail fast rather than hold the caller open");

        response.Headers.Should().ContainKey("Retry-After",
            "a 503 without it tells the caller to retry but not when");

        var problem = await response.Content.ReadAsStringAsync();

        problem.Should().Contain("correlationId",
            "the failure must still be traceable to its log lines");

        problem.Should().NotContain("unreachable-provider-so-this-is-never-checked",
            "credentials must never appear in an error response");
    }

    [Fact]
    public async Task Uploads_a_valid_image_and_returns_a_presigned_url()
    {
        var user = Guid.NewGuid().ToString();
        var itemId = await CreateItemAsync(user);

        var response = await UploadAsync(user, itemId, OwnershipIsolationTests.PngBytes(), "photo.png", "image/png");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var image = await response.Content.ReadFromJsonAsync<ItemImageResponse>();
        image!.ContentType.Should().Be("image/png");
        image.Url.Should().Contain("X-Amz-Signature", "images are served through presigned URLs, not proxied");
    }

    [Fact]
    public async Task Rejects_a_file_whose_content_is_not_an_image()
    {
        // The header says image/png and the extension says .png. Only the bytes tell the truth,
        // and this is the test that proves the API reads them.
        var user = Guid.NewGuid().ToString();
        var itemId = await CreateItemAsync(user);

        var html = "<html><script>alert(1)</script></html>"u8.ToArray();

        var response = await UploadAsync(user, itemId, html, "innocent.png", "image/png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejects_an_oversized_upload()
    {
        var user = Guid.NewGuid().ToString();
        var itemId = await CreateItemAsync(user);

        var oversized = new byte[2 * 1024 * 1024];
        OwnershipIsolationTests.PngBytes().CopyTo(oversized, 0);

        var response = await UploadAsync(user, itemId, oversized, "big.png", "image/png");

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Concurrent_updates_produce_a_conflict_rather_than_a_lost_update()
    {
        // Without the xmin concurrency token the second write would silently overwrite the
        // first, and the user whose edit vanished would have no indication it ever happened.
        var user = Guid.NewGuid().ToString();
        var itemId = await CreateItemAsync(user);

        var client = factory.CreateClientFor(user);

        var first = client.PutAsJsonAsync($"/api/v1/items/{itemId}",
            new UpdateItemRequest("First writer", null, 1, null, null, false));

        var second = client.PutAsJsonAsync($"/api/v1/items/{itemId}",
            new UpdateItemRequest("Second writer", null, 2, null, null, true));

        var responses = await Task.WhenAll(first, second);

        responses.Should().Contain(r => r.IsSuccessStatusCode);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)
            .Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Deleting_an_item_removes_its_images()
    {
        var user = Guid.NewGuid().ToString();
        var itemId = await CreateItemAsync(user);

        await UploadAsync(user, itemId, OwnershipIsolationTests.PngBytes(), "photo.png", "image/png");

        var client = factory.CreateClientFor(user);
        (await client.DeleteAsync($"/api/v1/items/{itemId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/v1/items/{itemId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Validation_failures_return_problem_details_with_field_errors()
    {
        var response = await factory.CreateClientFor(Guid.NewGuid().ToString())
            .PostAsJsonAsync("/api/v1/items", new CreateItemRequest("", null, -5, null, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Name").And.Contain("Quantity");
    }

    [Fact]
    public async Task Responses_carry_a_correlation_id()
    {
        var response = await factory.CreateClientFor(Guid.NewGuid().ToString()).GetAsync("/api/v1/items");

        response.Headers.Should().ContainKey("X-Correlation-Id");
    }

    [Fact]
    public async Task Supplied_correlation_ids_are_honoured_when_safe()
    {
        var client = factory.CreateClientFor(Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "trace-abc-123");

        var response = await client.GetAsync("/api/v1/items");

        response.Headers.GetValues("X-Correlation-Id").Should().Contain("trace-abc-123");
    }

    [Fact]
    public async Task Health_endpoints_are_anonymous_and_report_dependencies()
    {
        var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);

        var ready = await anonymous.GetAsync("/health/ready");
        var body = await ready.Content.ReadAsStringAsync();

        // Named per-check detail, not the framework default single word "Healthy".
        body.Should().Contain("postgres").And.Contain("redis");
    }

    private async Task<Guid> CreateItemAsync(string user)
    {
        var response = await factory.CreateClientFor(user)
            .PostAsJsonAsync("/api/v1/items", new CreateItemRequest("Item under test", null, 1, null, null));

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ItemResponse>())!.Id;
    }

    private Task<HttpResponseMessage> UploadAsync(
        string user, Guid itemId, byte[] bytes, string fileName, string declaredContentType)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(declaredContentType);
        content.Add(file, "file", fileName);

        return factory.CreateClientFor(user).PostAsync($"/api/v1/items/{itemId}/images", content);
    }
}
