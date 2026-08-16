using System.Globalization;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ShoppingList.Api.Common.Validation;

/// <summary>
/// Restates each endpoint's request-body limits in its description, in prose.
/// <para>
/// <see cref="ValidationSchemaTransformer"/> already publishes these as machine-readable schema
/// constraints, which is what a code generator or contract test consumes. This exists for the
/// other audience: a person reading the rendered documentation, for whom the constraint is only
/// useful if the UI happens to surface that part of the schema. Rendering support varies between
/// documentation clients and between their versions; an operation description is the one field
/// every client displays. Publishing the same fact in both places is the difference between a
/// limit that is discoverable and one that is merely present.
/// </para>
/// <para>
/// Generated from the validators for the same reason the schema constraints are. Prose typed by
/// hand next to a rule is prose that outlives the rule — and a description claiming a limit the
/// API no longer enforces misleads more effectively than silence would.
/// </para>
/// </summary>
internal sealed class ConstraintDescriptionTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Body-bound parameters only. Multipart uploads bind as IFormFile from the form source
        // and have no validator, and query and path parameters carry their own descriptions.
        var bodyType = context.Description.ParameterDescriptions
            .FirstOrDefault(parameter => parameter.Source == BindingSource.Body)?.Type;

        if (bodyType is null)
        {
            return Task.CompletedTask;
        }

        var validatorType = typeof(IValidator<>).MakeGenericType(bodyType);

        if (context.ApplicationServices.GetService(validatorType) is not IValidator validator)
        {
            return Task.CompletedTask;
        }

        var limits = new List<string>();

        foreach (var member in validator.CreateDescriptor().GetMembersWithValidators())
        {
            var rules = Collect(member);
            var described = Describe(rules);

            if (described is not null)
            {
                // Camel case for the same reason the schema transformer converts: FluentValidation
                // reports the CLR member name, but the caller sees the JSON name.
                limits.Add($"`{JsonNamingPolicy.CamelCase.ConvertName(member.Key)}` {described}");
            }
        }

        if (limits.Count == 0)
        {
            return Task.CompletedTask;
        }

        var summary = "**Limits:** " + string.Join(" · ", limits);

        // Appended, never replaced. Several endpoints carry hand-written descriptions explaining
        // a design decision — the ROPC grant, the magic-byte detection — and those are worth more
        // than this is.
        operation.Description = string.IsNullOrWhiteSpace(operation.Description)
            ? summary
            : $"{operation.Description}\n\n{summary}";

        return Task.CompletedTask;
    }

    /// <summary>
    /// Folds every rule declared for one member into a single set of bounds. A member can carry
    /// several — <c>NotEmpty().MaximumLength(200)</c> is two — and they describe one field
    /// together rather than separately.
    /// </summary>
    private static Bounds Collect(IEnumerable<(IPropertyValidator Validator, IRuleComponent Options)> member)
    {
        var bounds = new Bounds();

        foreach (var (propertyValidator, _) in member)
        {
            switch (propertyValidator)
            {
                case INotEmptyValidator:
                    bounds.NotEmpty = true;
                    break;

                case ILengthValidator length:
                    if (length.Min > 0)
                    {
                        bounds.MinLength = length.Min;
                    }

                    if (length.Max > 0)
                    {
                        bounds.MaxLength = length.Max;
                    }

                    break;

                case IEmailValidator:
                    bounds.Email = true;
                    break;

                case IRegularExpressionValidator regex:
                    bounds.Pattern = regex.Expression;
                    break;

                case IComparisonValidator comparison
                    when comparison.ValueToCompare is IConvertible convertible:
                    var value = convertible.ToDecimal(CultureInfo.InvariantCulture);

                    switch (comparison.Comparison)
                    {
                        case Comparison.GreaterThan:
                            bounds.Minimum = value;
                            bounds.MinimumExclusive = true;
                            break;

                        case Comparison.GreaterThanOrEqual:
                            bounds.Minimum = value;
                            break;

                        case Comparison.LessThan:
                            bounds.Maximum = value;
                            bounds.MaximumExclusive = true;
                            break;

                        case Comparison.LessThanOrEqual:
                            bounds.Maximum = value;
                            break;

                        case Comparison.Equal:
                        case Comparison.NotEqual:
                        default:
                            break;
                    }

                    break;
            }
        }

        return bounds;
    }

    /// <summary>
    /// Renders one member's bounds as a phrase, or null when there is nothing a reader would
    /// benefit from being told.
    /// </summary>
    private static string? Describe(Bounds bounds)
    {
        var parts = new List<string>();

        // NotEmpty on a string is a minimum length of one. It is only treated that way when no
        // numeric bound was found, because on a number NotEmpty means "not the default value",
        // which is a different claim and would render as a length that does not apply.
        var minLength = bounds.MinLength
                        ?? (bounds.NotEmpty && bounds.Minimum is null ? 1 : null);

        if (minLength is not null && bounds.MaxLength is not null)
        {
            parts.Add($"{minLength}–{bounds.MaxLength} characters");
        }
        else if (bounds.MaxLength is not null)
        {
            parts.Add($"at most {bounds.MaxLength} characters");
        }
        else if (minLength is not null)
        {
            parts.Add($"at least {minLength} characters");
        }

        if (bounds.Minimum is not null || bounds.Maximum is not null)
        {
            var numeric = new List<string>();

            if (bounds.Minimum is not null)
            {
                numeric.Add(bounds.MinimumExclusive
                    ? $"greater than {Format(bounds.Minimum.Value)}"
                    : $"at least {Format(bounds.Minimum.Value)}");
            }

            if (bounds.Maximum is not null)
            {
                numeric.Add(bounds.MaximumExclusive
                    ? $"less than {Format(bounds.Maximum.Value)}"
                    : $"at most {Format(bounds.Maximum.Value)}");
            }

            parts.Add(string.Join(", ", numeric));
        }

        if (bounds.Email)
        {
            parts.Add("a valid email address");
        }

        if (bounds.Pattern is not null)
        {
            parts.Add($"matching `{bounds.Pattern}`");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Trims the trailing zeros a decimal carries so 100000m reads as 100000.</summary>
    private static string Format(decimal value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);

    private sealed class Bounds
    {
        public bool NotEmpty { get; set; }

        public int? MinLength { get; set; }

        public int? MaxLength { get; set; }

        public decimal? Minimum { get; set; }

        public bool MinimumExclusive { get; set; }

        public decimal? Maximum { get; set; }

        public bool MaximumExclusive { get; set; }

        public bool Email { get; set; }

        public string? Pattern { get; set; }
    }
}
