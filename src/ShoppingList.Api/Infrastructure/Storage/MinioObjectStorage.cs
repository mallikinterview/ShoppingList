using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using ShoppingList.Api.Common.Errors;
using ShoppingList.Api.Configuration;

namespace ShoppingList.Api.Infrastructure.Storage;

public interface IObjectStorage
{
    Task<string> UploadAsync(string objectKey, Stream content, string contentType, long size, CancellationToken ct);

    Task<string> GetPresignedDownloadUrlAsync(string objectKey, CancellationToken ct);

    Task DeleteAsync(string objectKey, CancellationToken ct);

    Task EnsureBucketAsync(CancellationToken ct);

    /// <summary>Builds the object key. Every segment is server-generated — nothing from the
    /// client's filename appears in it, so traversal and collision are impossible by
    /// construction rather than by validation.</summary>
    static string BuildKey(Guid userId, Guid itemId, string contentType) =>
        $"{userId:N}/{itemId:N}/{Guid.CreateVersion7():N}{ContentTypeDetector.ExtensionFor(contentType)}";
}

internal sealed class MinioObjectStorage(
    IMinioClient client,
    IOptions<MinioSettings> options,
    ILogger<MinioObjectStorage> logger) : IObjectStorage
{
    private readonly MinioSettings _settings = options.Value;

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        var exists = await client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(_settings.BucketName), ct);

        if (exists)
        {
            return;
        }

        await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_settings.BucketName), ct);

        logger.LogInformation("Created bucket {Bucket}.", _settings.BucketName);

        // No public-read policy is applied. The bucket stays private and every object is reached
        // through a short-lived presigned URL scoped to that one object. A public bucket would
        // make every user's images readable by anyone who can guess or enumerate a key, which
        // no amount of authorization in the API would prevent.
    }

    public async Task<string> UploadAsync(
        string objectKey,
        Stream content,
        string contentType,
        long size,
        CancellationToken ct)
    {
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(_settings.BucketName)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(size)
            .WithContentType(contentType), ct);

        logger.LogInformation("Stored object {ObjectKey} ({Size} bytes).", objectKey, size);

        return objectKey;
    }

    /// <summary>
    /// Returns a time-limited URL the caller fetches directly from object storage.
    /// <para>
    /// The API never streams image bytes itself. Proxying would put every megabyte through the
    /// application's thread pool and network stack for no benefit, and would make image traffic
    /// compete with API traffic for the same limited resources.
    /// </para>
    /// <para>
    /// These URLs are generated per response and never persisted or cached — a presigned URL is
    /// a bearer credential with an expiry, so a stored one becomes a broken link, and a cached
    /// one is a credential sitting in a cache.
    /// </para>
    /// </summary>
    public async Task<string> GetPresignedDownloadUrlAsync(string objectKey, CancellationToken ct)
    {
        try
        {
            var url = await client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
                .WithBucket(_settings.BucketName)
                .WithObject(objectKey)
                .WithExpiry(_settings.PresignedUrlExpirySeconds));

            // The client signs against the endpoint it is configured with — the internal Docker
            // service name — but the URL is consumed by the caller's browser, which cannot
            // resolve it. Swapping the host preserves the signature (it covers the path and
            // query, not the host) while making the URL reachable.
            return SwapToPublicEndpoint(url);
        }
        catch (MinioException ex)
        {
            logger.LogError(ex, "Could not presign a URL for {ObjectKey}.", objectKey);
            throw new DependencyUnavailableException("minio", ex);
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        try
        {
            await client.RemoveObjectAsync(new RemoveObjectArgs()
                .WithBucket(_settings.BucketName)
                .WithObject(objectKey), ct);
        }
        catch (ObjectNotFoundException ex)
        {
            // Already gone is the desired end state. Treating it as an error would make delete
            // non-idempotent and break every retry.
            logger.LogDebug(ex, "Object {ObjectKey} was already absent.", objectKey);
        }
    }

    private string SwapToPublicEndpoint(string url)
    {
        var scheme = _settings.UseSsl ? "https" : "http";
        var internalPrefix = $"{scheme}://{_settings.Endpoint}";
        var publicPrefix = $"{scheme}://{_settings.PublicEndpoint}";

        return url.StartsWith(internalPrefix, StringComparison.OrdinalIgnoreCase)
            ? string.Concat(publicPrefix, url.AsSpan(internalPrefix.Length))
            : url;
    }
}

/// <summary>
/// Creates the bucket at startup, idempotently.
/// <para>
/// Done in the application rather than by an init container so the same guarantee holds
/// everywhere the API runs — Compose, a test container, a cloud environment — without a separate
/// provisioning step that can be forgotten or diverge.
/// </para>
/// </summary>
internal sealed class ObjectStorageInitializer(
    IObjectStorage storage,
    ILogger<ObjectStorageInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await storage.EnsureBucketAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Logged, not thrown. Object storage being briefly unavailable at boot should not
            // stop the API from serving every endpoint that has nothing to do with images; the
            // readiness check reports it and uploads fail explicitly until it recovers.
            logger.LogError(ex, "Could not ensure the storage bucket exists at startup. Uploads will fail until it is available.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
