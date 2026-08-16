using FluentValidation;
using ShoppingList.Api.Common.Extensions;
using ShoppingList.Api.Data.Entities;

namespace ShoppingList.Api.Features.Items;

/// <summary>
/// Request DTOs are separate types from entities, and separate again from responses.
/// <para>
/// Not ceremony: binding directly to <see cref="ShoppingItem"/> would let a client post
/// <c>userId</c>, <c>id</c> or <c>embedding</c> and have them honoured — mass assignment, and
/// specifically an ownership-transfer vulnerability. The request type physically cannot carry
/// fields the client is not allowed to set.
/// </para>
/// </summary>
public sealed record CreateItemRequest(
    [property: OpenApiExample("Whole milk")] string Name,
    [property: OpenApiExample("Prefer the organic one")] string? Notes,
    [property: OpenApiExample(2)] int Quantity,
    [property: OpenApiExample("litres")] string? Unit,
    [property: OpenApiExample("Dairy")] string? Category);

public sealed record UpdateItemRequest(
    [property: OpenApiExample("Whole milk")] string Name,
    [property: OpenApiExample("Prefer the organic one")] string? Notes,
    [property: OpenApiExample(2)] int Quantity,
    [property: OpenApiExample("litres")] string? Unit,
    [property: OpenApiExample("Dairy")] string? Category,
    [property: OpenApiExample(false)] bool IsPurchased);

public sealed record ItemImageResponse(
    Guid Id,
    string ContentType,
    long SizeBytes,
    string? OriginalFileName,
    string Url,
    DateTimeOffset CreatedAt);

/// <summary>
/// Note what is absent: <c>Embedding</c>. Serialising a 768-element float array onto every item
/// in every list response would multiply payload size by roughly fifty for data no client can
/// use. Response DTOs make that omission structural rather than a habit.
/// </summary>
public sealed record ItemResponse(
    Guid Id,
    string Name,
    string? Notes,
    int Quantity,
    string? Unit,
    string? Category,
    bool IsPurchased,
    string EmbeddingStatus,
    IReadOnlyList<ItemImageResponse> Images,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed class CreateItemRequestValidator : AbstractValidator<CreateItemRequest>
{
    public CreateItemRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Notes)
            .MaximumLength(2000);

        // Upper bound as well as lower. Unbounded integers reach the database as-is, and a
        // quantity of int.MaxValue is not a shopping list entry — it is either a mistake or
        // someone probing for an overflow.
        RuleFor(x => x.Quantity)
            .GreaterThan(0)
            .LessThanOrEqualTo(100_000);

        RuleFor(x => x.Unit).MaximumLength(32);
        RuleFor(x => x.Category).MaximumLength(64);
    }
}

internal sealed class UpdateItemRequestValidator : AbstractValidator<UpdateItemRequest>
{
    public UpdateItemRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Notes).MaximumLength(2000);
        RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(100_000);
        RuleFor(x => x.Unit).MaximumLength(32);
        RuleFor(x => x.Category).MaximumLength(64);
    }
}
