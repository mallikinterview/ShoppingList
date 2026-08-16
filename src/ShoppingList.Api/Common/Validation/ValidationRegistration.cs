using FluentValidation;

namespace ShoppingList.Api.Common.Validation;

public static class ValidationRegistration
{
    /// <summary>
    /// Registers every <see cref="IValidator{T}"/> in this assembly, plus the open generic
    /// <see cref="ValidationFilter{TRequest}"/> so endpoints can attach validation with
    /// <c>.WithValidation&lt;TRequest&gt;()</c> and nothing else.
    /// <para>
    /// Assembly scanning is used deliberately in this one place: adding a request type and
    /// forgetting to register its validator would mean the endpoint silently accepts anything,
    /// and a silent absence of validation is far worse than the small amount of magic here.
    /// </para>
    /// </summary>
    public static IServiceCollection AddValidatorsFromApplicationAssembly(this IServiceCollection services)
    {
        // includeInternalTypes is required, not optional. The scanner registers only PUBLIC
        // validators by default, and every validator here is internal — nothing outside this
        // assembly has any business constructing one. Without the flag the scan finds nothing,
        // registration silently succeeds, and the first request to a validated endpoint fails
        // with "unable to resolve IValidator<T>" at runtime. A DI misconfiguration that fails at
        // request time rather than at startup is exactly the failure mode ValidateOnStart exists
        // to prevent elsewhere, which is why the test below pins it.
        services.AddValidatorsFromAssemblyContaining<Program>(
            ServiceLifetime.Singleton,
            includeInternalTypes: true);
        services.AddScoped(typeof(ValidationFilter<>));

        return services;
    }
}
