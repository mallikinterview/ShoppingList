using System.ComponentModel.DataAnnotations;

namespace ShoppingList.Api.Configuration;

public sealed class MinioSettings
{
    public const string SectionName = "MinioSettings";

    /// <summary>Internal endpoint the API connects to (service name inside the Docker network).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Host-visible endpoint baked into presigned URLs. Presigned URLs are consumed by the
    /// caller's browser, which cannot resolve internal service names — so this differs from
    /// <see cref="Endpoint"/> on purpose. Getting this wrong produces URLs that are perfectly
    /// valid and completely unreachable.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string PublicEndpoint { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string AccessKey { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string SecretKey { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string BucketName { get; init; } = "shopping-list-images";

    public bool UseSsl { get; init; }

    /// <summary>Short-lived by design: a presigned URL is a bearer credential for one object.</summary>
    [Range(60, 604800)]
    public int PresignedUrlExpirySeconds { get; init; } = 900;

    [Range(1024, 52428800)]
    public long MaxUploadBytes { get; init; } = 5 * 1024 * 1024;
}
