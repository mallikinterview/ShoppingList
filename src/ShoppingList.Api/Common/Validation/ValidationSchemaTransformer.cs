using System.Globalization;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Validators;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ShoppingList.Api.Common.Validation;

/// <summary>
/// Publishes each request type's FluentValidation rules as OpenAPI schema constraints.
/// <para>
/// Without this the document describes every field as an unconstrained string or integer, and
/// the actual limits — a 200-character name, a quantity between 1 and 100,000, a query capped
/// at 500 characters — exist only inside validators the caller cannot see. A consumer then
/// discovers them by collecting 400s, which is a poor substitute for documentation and makes
/// requirement 7's "all inputs are validated" invisible from outside.
/// </para>
/// <para>
/// Derived rather than duplicated. Annotating the contracts by hand would have been simpler and
/// would have created two descriptions of the same rule, free to drift — and a published limit
/// the API does not enforce is worse than no published limit at all. Reading the validators
/// means the document cannot disagree with the behaviour: change a rule and the schema changes
/// with it.
/// </para>
/// </summary>
internal sealed class ValidationSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (schema.Properties is null || schema.Properties.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Only request types have validators registered. Everything else — responses, nested
        // records, framework types — falls through untouched.
        var validatorType = typeof(IValidator<>).MakeGenericType(context.JsonTypeInfo.Type);

        if (context.ApplicationServices.GetService(validatorType) is not IValidator validator)
        {
            return Task.CompletedTask;
        }

        foreach (var member in validator.CreateDescriptor().GetMembersWithValidators())
        {
            // FluentValidation reports the CLR member name; the schema is keyed by the JSON
            // name. The API uses the default camelCase policy, so converting is enough.
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(member.Key);

            if (!schema.Properties.TryGetValue(jsonName, out var property) ||
                property is not OpenApiSchema target)
            {
                continue;
            }

            foreach (var (propertyValidator, _) in member)
            {
                Apply(target, propertyValidator);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Matched on FluentValidation's interfaces rather than its concrete validator classes.
    /// The concrete names change between major versions; the interfaces have not.
    /// </summary>
    private static void Apply(OpenApiSchema schema, IPropertyValidator validator)
    {
        switch (validator)
        {
            // NotEmpty on a string means "at least one character". On other types it means
            // "not the default value", which has no schema equivalent — so it is skipped
            // rather than mistranslated into a numeric bound.
            case INotEmptyValidator when IsString(schema):
                schema.MinLength = 1;
                break;

            // Covers MaximumLength, MinimumLength and Length alike. Each leaves the bound it
            // does not set at zero, so both are guarded.
            case ILengthValidator length:
                if (length.Min > 0)
                {
                    schema.MinLength = length.Min;
                }

                if (length.Max > 0)
                {
                    schema.MaxLength = length.Max;
                }

                break;

            case IRegularExpressionValidator regex:
                schema.Pattern = regex.Expression;
                break;

            case IEmailValidator:
                schema.Format = "email";
                break;

            case IComparisonValidator comparison:
                ApplyComparison(schema, comparison);
                break;
        }
    }

    /// <summary>
    /// OpenAPI 3.1 follows JSON Schema 2020-12, where <c>exclusiveMinimum</c> and
    /// <c>exclusiveMaximum</c> hold the bound themselves rather than acting as boolean
    /// modifiers on <c>minimum</c>. So a strict bound sets only the exclusive property, and an
    /// inclusive one sets only the plain property — setting both would publish the same limit
    /// twice and let them disagree.
    /// <para>
    /// Written as invariant strings because that is how the model represents numeric bounds:
    /// JSON Schema allows arbitrary precision, which no fixed .NET numeric type covers.
    /// </para>
    /// </summary>
    private static void ApplyComparison(OpenApiSchema schema, IComparisonValidator comparison)
    {
        if (comparison.ValueToCompare is not IConvertible convertible)
        {
            return;
        }

        var bound = convertible.ToDecimal(CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);

        switch (comparison.Comparison)
        {
            case Comparison.GreaterThan:
                schema.ExclusiveMinimum = bound;
                break;

            case Comparison.GreaterThanOrEqual:
                schema.Minimum = bound;
                break;

            case Comparison.LessThan:
                schema.ExclusiveMaximum = bound;
                break;

            case Comparison.LessThanOrEqual:
                schema.Maximum = bound;
                break;

            case Comparison.Equal:
            case Comparison.NotEqual:
            default:
                // No schema equivalent worth publishing.
                break;
        }
    }

    private static bool IsString(OpenApiSchema schema) =>
        schema.Type?.ToString().Contains("string", StringComparison.OrdinalIgnoreCase) == true;
}
