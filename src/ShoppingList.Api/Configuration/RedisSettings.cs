using System.ComponentModel.DataAnnotations;

namespace ShoppingList.Api.Configuration;

public sealed class RedisSettings
{
    public const string SectionName = "RedisSettings";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Deliberately false. With AbortOnConnectFail=true the client throws on a failed initial
    /// connect and the API cannot start without Redis — turning a cache into a hard dependency.
    /// False lets the application boot and serve from the database while Redis is unavailable.
    /// </summary>
    public bool AbortOnConnectFail { get; init; }

    public bool UseSsl { get; init; }

    /// <summary>Key prefix. Namespacing per environment prevents a shared Redis serving stale
    /// keys across environments — the kind of bug that only shows up in staging.</summary>
    [Required(AllowEmptyStrings = false)]
    public string InstanceName { get; init; } = "shoppinglist";

    [Range(1, 86400)]
    public int DefaultTtlSeconds { get; init; } = 300;

    /// <summary>
    /// Random jitter added to each TTL. Without it, keys written in the same burst expire in the
    /// same burst, and every one of them stampedes the database simultaneously.
    /// </summary>
    [Range(0, 3600)]
    public int TtlJitterSeconds { get; init; } = 60;
}
