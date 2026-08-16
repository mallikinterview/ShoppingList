using System.ComponentModel.DataAnnotations;

namespace ShoppingList.Api.Configuration;

public sealed class OllamaSettings
{
    public const string SectionName = "OllamaSettings";

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string EmbeddingModel { get; init; } = "nomic-embed-text";

    /// <summary>
    /// Asserted against the pgvector column width at startup. A mismatch is otherwise discovered
    /// as an opaque Postgres error on the first write, long after the misconfiguration.
    /// </summary>
    [Range(1, 4096)]
    public int EmbeddingDimensions { get; init; } = 768;

    /// <summary>
    /// Explicit because <see cref="HttpClient"/> defaults to 100 seconds — long enough that a
    /// stalled model server looks like a hung API rather than a failing dependency.
    /// </summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 30;

    [Range(0, 10)]
    public int MaxRetries { get; init; } = 3;
}
