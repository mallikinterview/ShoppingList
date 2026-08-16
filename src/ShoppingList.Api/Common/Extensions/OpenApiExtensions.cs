using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using ShoppingList.Api.Common.Validation;

namespace ShoppingList.Api.Common.Extensions;

/// <summary>
/// Publishes the API's authentication scheme and its validation rules in the OpenAPI document.
/// <para>
/// Without the security scheme the document describes every route and schema but never says a
/// token is required, so the contract is wrong in a way that is invisible from the code: the
/// endpoints enforce authorization correctly, and the document simply does not mention it.
/// Scalar renders its authorization panel from <c>securitySchemes</c>, so the omission also
/// means there is no way to authenticate from the UI at all — the endpoints are all there, and
/// every one of them answers 401.
/// </para>
/// <para>
/// The requirement is attached per operation rather than to the whole document, because a
/// blanket requirement would also mark signup, token, refresh, the health endpoints and the
/// metrics scrape as protected. Those are anonymous on purpose, and a document that claims
/// otherwise sends a reader looking for a token they cannot yet have.
/// </para>
/// </summary>
internal static class OpenApiExtensions
{
    private const string SchemeName = "Bearer";

    public static IServiceCollection AddApplicationOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

                document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description =
                        "Paste the accessToken returned by POST /api/v1/auth/token. " +
                        "Tokens are issued by Keycloak and expire after five minutes."
                };

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                // AllowAnonymous wins over any authorization metadata, exactly as it does at
                // runtime — so the document and the pipeline agree on which endpoints are open.
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;

                if (metadata.OfType<IAllowAnonymous>().Any())
                {
                    return Task.CompletedTask;
                }

                if (!metadata.OfType<IAuthorizeData>().Any())
                {
                    return Task.CompletedTask;
                }

                // The host document is not optional here, though the constructor makes it look
                // that way. A reference with no document cannot resolve its own target, and it
                // serialises as an empty requirement object — "security": [ { } ] — which
                // OpenAPI reads as "no authentication required". The document then states the
                // exact opposite of the truth, and does so silently: the scheme is present, the
                // operations are present, and every reader concludes the endpoints are public.
                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = []
                    }
                ];

                return Task.CompletedTask;
            });

            // Turns each request type's FluentValidation rules into schema constraints, so the
            // published contract carries the limits the API actually enforces rather than
            // describing every field as an unconstrained string. See ValidationSchemaTransformer
            // for why these are derived from the validators instead of annotated by hand.
            options.AddSchemaTransformer<ValidationSchemaTransformer>();
            options.AddSchemaTransformer<ExampleSchemaTransformer>();
            options.AddOperationTransformer<ItemQueryParameterTransformer>();
            options.AddOperationTransformer<ConstraintDescriptionTransformer>();
        });
}
