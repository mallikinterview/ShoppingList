using System.ComponentModel.DataAnnotations;

namespace ShoppingList.Api.Configuration;

/// <summary>
/// Three policies rather than one global limit, because the endpoints have different threat
/// models: auth endpoints are the credential-stuffing surface, uploads are the bandwidth and
/// storage surface, and everything else is ordinary API traffic.
/// </summary>
public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimitSettings";

    [Range(1, 100000)]
    public int PermitLimit { get; init; } = 100;

    [Range(1, 3600)]
    public int WindowSeconds { get; init; } = 60;

    [Range(1, 10000)]
    public int AuthPermitLimit { get; init; } = 10;

    [Range(1, 3600)]
    public int AuthWindowSeconds { get; init; } = 60;

    [Range(1, 10000)]
    public int UploadPermitLimit { get; init; } = 20;

    [Range(1, 3600)]
    public int UploadWindowSeconds { get; init; } = 60;
}
