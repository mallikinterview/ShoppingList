using ShoppingList.Api.Data;

namespace ShoppingList.Tests.Unit;

/// <summary>
/// The naming convention is load-bearing: the hybrid search statement is hand-written SQL that
/// refers to columns by their snake_case names. If a future property maps to a PascalCase column,
/// the build stays green, the migration is valid, and search fails at runtime with "column
/// user_id does not exist". These tests pin the transformation so that cannot happen quietly.
/// </summary>
public sealed class SnakeCaseNamingTests
{
    [Theory]
    [InlineData("SearchVector", "search_vector")]
    [InlineData("UserId", "user_id")]
    [InlineData("IsPurchased", "is_purchased")]
    [InlineData("EmbeddingStatus", "embedding_status")]
    [InlineData("CreatedAt", "created_at")]
    [InlineData("OriginalFileName", "original_file_name")]
    public void Converts_pascal_case_to_snake_case(string input, string expected) =>
        AppDbContext.ToSnakeCase(input).Should().Be(expected);

    [Theory]
    [InlineData("shopping_items")]
    [InlineData("ix_shopping_items_user_created")]
    [InlineData("ux_users_subject_id")]
    [InlineData("xmin")]
    public void Is_idempotent_for_names_already_configured_explicitly(string name) =>
        AppDbContext.ToSnakeCase(name).Should().Be(name,
            "explicitly configured table and index names must pass through untouched");

    [Fact]
    public void Does_not_split_acronyms_into_single_letters() =>
        AppDbContext.ToSnakeCase("URLPath").Should().Be("url_path");

    [Fact]
    public void Handles_empty_and_single_character_names()
    {
        AppDbContext.ToSnakeCase(string.Empty).Should().BeEmpty();
        AppDbContext.ToSnakeCase("A").Should().Be("a");
    }
}
