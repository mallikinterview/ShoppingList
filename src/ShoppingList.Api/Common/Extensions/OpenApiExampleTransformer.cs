using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ShoppingList.Api.Common.Extensions;

/// <summary>
/// Declares the sample value OpenAPI publishes for a single property.
/// <para>
/// Without one, the generator falls back to the type's default — <c>null</c> for a nullable
/// reference type. That is a truthful example but a poor one: it tells a reader what the field
/// may be rather than what it is for, and it forces anyone trying the endpoint from the browser
/// to hand-type the surrounding quotes. Declaring the example next to the property keeps it in
/// the same place as the type and the validation rule, so all three move together.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
internal sealed class OpenApiExampleAttribute(object value) : Attribute
{
    public object Value { get; } = value;
}

/// <summary>
/// Copies <see cref="OpenApiExampleAttribute"/> values onto the generated schema.
/// <para>
/// Runs per property schema, so it composes with <c>ValidationSchemaTransformer</c> rather than
/// competing with it: that one publishes the constraints, this one publishes a value that
/// satisfies them.
/// </para>
/// </summary>
internal sealed class ExampleSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var attribute = context.JsonPropertyInfo?.AttributeProvider
            ?.GetCustomAttributes(typeof(OpenApiExampleAttribute), inherit: false)
            .OfType<OpenApiExampleAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            // Serialised through the property's own runtime type so an int example emits as a
            // JSON number and a string as a quoted string, rather than everything as text.
            schema.Example = JsonSerializer.SerializeToNode(
                attribute.Value,
                attribute.Value.GetType());
        }

        return Task.CompletedTask;
    }
}
