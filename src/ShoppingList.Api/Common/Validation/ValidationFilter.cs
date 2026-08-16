using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ShoppingList.Api.Common.Validation;

/// <summary>
/// Endpoint filter that validates the request body before the handler runs.
/// <para>
/// Validation lives at the boundary, not inside handlers. A handler that begins with argument
/// checks has two jobs, and the checks drift out of sync with the documented contract. Rejecting
/// here means every handler can assume its input is structurally valid, and every validation
/// failure produces the same response shape.
/// </para>
/// <para>
/// Failures return 400 with <c>ValidationProblemDetails</c> — the same problem+json envelope as
/// every other error, with per-field messages. A caller should never have to parse two different
/// error formats from one API.
/// </para>
/// </summary>
public sealed class ValidationFilter<TRequest> : IEndpointFilter
    where TRequest : class
{
    private readonly IValidator<TRequest> _validator;

    public ValidationFilter(IValidator<TRequest> validator) => _validator = validator;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();

        if (request is null)
        {
            return TypedResults.Problem(
                title: "Invalid request",
                detail: "The request body could not be read.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _validator.ValidateAsync(request, context.HttpContext.RequestAborted);

        if (result.IsValid)
        {
            return await next(context);
        }

        var errors = result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                group => group.Key,
                group => group.Select(e => e.ErrorMessage).ToArray());

        return TypedResults.ValidationProblem(
            errors,
            detail: "One or more validation errors occurred.",
            title: "Validation failed");
    }
}

public static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder WithValidation<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class =>
        builder
            .AddEndpointFilter<ValidationFilter<TRequest>>()
            .ProducesValidationProblem();
}
