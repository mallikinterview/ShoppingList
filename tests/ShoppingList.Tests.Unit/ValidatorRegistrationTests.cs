using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using ShoppingList.Api.Common.Validation;
using ShoppingList.Api.Features.Auth;
using ShoppingList.Api.Features.Items;
using ShoppingList.Api.Features.Search;

namespace ShoppingList.Tests.Unit;

/// <summary>
/// Guards a failure mode that is invisible until runtime.
/// <para>
/// Endpoints attach validation through an endpoint filter, which resolves
/// <c>IValidator&lt;TRequest&gt;</c> from the container. If a validator is not registered the
/// application still starts perfectly happily, and the first request to that endpoint returns a
/// 500 from inside the DI container. Nothing at build time or startup catches it.
/// </para>
/// <para>
/// The specific trap: FluentValidation's assembly scanner registers only public validators
/// unless told otherwise, and every validator in this codebase is internal.
/// </para>
/// </summary>
public sealed class ValidatorRegistrationTests
{
    public static TheoryData<Type> ValidatedRequestTypes =>
    [
        typeof(SignupRequest),
        typeof(TokenRequest),
        typeof(RefreshRequest),
        typeof(CreateItemRequest),
        typeof(UpdateItemRequest),
        typeof(SearchRequest)
    ];

    [Theory]
    [MemberData(nameof(ValidatedRequestTypes))]
    public void Every_validated_request_type_has_a_resolvable_validator(Type requestType)
    {
        var provider = new ServiceCollection()
            .AddValidatorsFromApplicationAssembly()
            .BuildServiceProvider();

        var validatorType = typeof(IValidator<>).MakeGenericType(requestType);

        provider.GetService(validatorType).Should().NotBeNull(
            "endpoints resolve IValidator<{0}> at request time, so a missing registration is a 500 " +
            "that no build or startup check would have caught",
            requestType.Name);
    }

    [Fact]
    public void Validation_filter_is_registered_as_an_open_generic()
    {
        var provider = new ServiceCollection()
            .AddValidatorsFromApplicationAssembly()
            .BuildServiceProvider();

        provider.GetService<ValidationFilter<TokenRequest>>().Should().NotBeNull();
    }
}
