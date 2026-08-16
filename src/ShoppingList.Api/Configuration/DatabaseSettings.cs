using System.ComponentModel.DataAnnotations;

namespace ShoppingList.Api.Configuration;

public sealed class DatabaseSettings
{
    public const string SectionName = "DatabaseSettings";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;

    [Range(1, 500)]
    public int MaxPoolSize { get; init; } = 50;

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; init; } = 30;
}
