using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Infrastructure.Identity;

namespace ShoppingList.Api.Data;

/// <summary>
/// A minimal host for the <c>--migrate-only</c> entry point.
/// <para>
/// The migrator deliberately does <b>not</b> reuse the API's composition root. Applying a schema
/// change needs a database connection and nothing else — it has no reason to require a reachable
/// identity provider, an object store, or an embedding model's URL. Booting the full application
/// would make a migration fail because Keycloak was misconfigured, which is both confusing and
/// an unnecessary coupling.
/// </para>
/// <para>
/// It also keeps the compose service honest: <c>migrator</c> is passed two settings, and those
/// two are genuinely all it consumes. Anything more would be configuration that exists only to
/// satisfy a startup check the job does not care about.
/// </para>
/// </summary>
internal static class MigrationHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddEnvironmentVariables();

        // Console only. The migrator is a short-lived job whose output belongs in the container
        // log where `docker compose logs migrator` will find it; shipping a handful of lines to
        // Loki would mean waiting on Loki to migrate a database.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger);

        // Only the two settings groups a migration actually depends on: where the database is,
        // and the embedding dimension the schema must agree with.
        builder.Services.AddOptions<DatabaseSettings>()
            .Bind(builder.Configuration.GetSection(DatabaseSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<OllamaSettings>()
            .Bind(builder.Configuration.GetSection(OllamaSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AppDbContext resolves ICurrentUser for its ownership query filter. There is no request
        // and no user here; the filter is never evaluated because migrations issue DDL rather
        // than queries, but the dependency still has to resolve.
        builder.Services.AddSingleton<ICurrentUser, MigrationCurrentUser>();
        builder.Services.AddPersistence(builder.Configuration);

        using var host = builder.Build();

        try
        {
            await host.RunDatabaseMigrationsAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Migration failed.");

            // Non-zero exit is what makes the compose dependency on
            // service_completed_successfully hold the API back, instead of it starting against
            // a half-migrated schema.
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private sealed class MigrationCurrentUser : ICurrentUser
    {
        public Guid UserId => Guid.Empty;

        public string SubjectId => string.Empty;

        public string? Username => null;

        public string? Email => null;

        public bool IsAuthenticated => false;
    }
}
