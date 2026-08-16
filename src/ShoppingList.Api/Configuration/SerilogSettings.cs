using System.ComponentModel.DataAnnotations;

namespace ShoppingList.Api.Configuration;

public sealed class SerilogSettings
{
    public const string SectionName = "SerilogSettings";

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string LokiUrl { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(Verbose|Debug|Information|Warning|Error|Fatal)$")]
    public string MinimumLevel { get; init; } = "Information";

    /// <summary>
    /// Becomes the <c>app</c> Loki label. Labels are kept to a handful of low-cardinality values
    /// (app, environment, level, ranking variant); user ids, item ids and correlation ids stay in
    /// the structured payload, where they remain queryable without multiplying Loki streams.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ApplicationName { get; init; } = "shopping-list-api";
}
