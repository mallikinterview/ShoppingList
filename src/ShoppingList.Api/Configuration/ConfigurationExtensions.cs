using Microsoft.Extensions.Options;

namespace ShoppingList.Api.Configuration;

public static class ConfigurationExtensions
{
    /// <summary>
    /// Binds and validates every settings class.
    /// <para>
    /// <c>ValidateOnStart</c> is the point of this method. Without it, a missing connection string
    /// or a malformed URL surfaces as a 500 on whichever request happens to touch it first —
    /// possibly hours after deployment, possibly for one endpoint only. With it, the process
    /// refuses to start and says exactly which key is wrong. Failing at boot is strictly better
    /// than failing in production traffic.
    /// </para>
    /// </summary>
    public static IServiceCollection AddApplicationSettings(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<DatabaseSettings>(configuration, DatabaseSettings.SectionName);
        services.AddValidatedOptions<RedisSettings>(configuration, RedisSettings.SectionName);
        services.AddValidatedOptions<MinioSettings>(configuration, MinioSettings.SectionName);
        services.AddValidatedOptions<KeycloakSettings>(configuration, KeycloakSettings.SectionName);
        services.AddValidatedOptions<OllamaSettings>(configuration, OllamaSettings.SectionName);
        services.AddValidatedOptions<SearchSettings>(configuration, SearchSettings.SectionName);
        services.AddValidatedOptions<RateLimitSettings>(configuration, RateLimitSettings.SectionName);
        services.AddValidatedOptions<SerilogSettings>(configuration, SerilogSettings.SectionName);

        // Cross-field rule that DataAnnotations cannot express: under the weighted strategy the
        // two weights must sum to 1, or scores are silently scaled and the ranking is meaningless.
        services.AddSingleton<IValidateOptions<SearchSettings>, SearchSettingsValidator>();

        return services;
    }

    private static void AddValidatedOptions<TSettings>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TSettings : class
    {
        services.AddOptions<TSettings>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    /// <summary>Convenience accessor for the composition root, where the container is not yet built.</summary>
    public static TSettings GetSettings<TSettings>(this IConfiguration configuration, string sectionName)
        where TSettings : class, new()
    {
        var settings = new TSettings();
        configuration.GetSection(sectionName).Bind(settings);
        return settings;
    }
}

internal sealed class SearchSettingsValidator : IValidateOptions<SearchSettings>
{
    public ValidateOptionsResult Validate(string? name, SearchSettings options)
    {
        var failures = new List<string>();

        var weightSum = options.VectorWeight + options.TextWeight;
        if (Math.Abs(weightSum - 1.0) > 0.001)
        {
            failures.Add(
                $"SearchSettings__VectorWeight + SearchSettings__TextWeight must equal 1.0 (found {weightSum:F3}). " +
                "Unnormalised weights rescale the fused score and make ranking comparisons between variants meaningless.");
        }

        if (options.Experiment.Enabled &&
            options.Experiment.ControlStrategy == options.Experiment.TreatmentStrategy)
        {
            failures.Add(
                "SearchSettings__Experiment__ControlStrategy and TreatmentStrategy are identical, " +
                "so the experiment compares a strategy against itself. Disable the experiment or choose different strategies.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
