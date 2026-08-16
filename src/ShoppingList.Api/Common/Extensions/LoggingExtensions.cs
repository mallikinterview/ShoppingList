using Serilog;
using Serilog.Events;
using Serilog.Sinks.Grafana.Loki;
using ShoppingList.Api.Configuration;

namespace ShoppingList.Api.Common.Extensions;

public static class LoggingExtensions
{
    /// <summary>
    /// Configures Serilog with a console sink and a direct Loki sink.
    /// <para>
    /// Logs are shipped straight to Loki rather than scraped from container stdout by Promtail.
    /// Scraping means Loki receives a rendered text line and has to re-parse structure back out
    /// of it; the direct sink preserves structured properties as queryable fields, so
    /// <c>UserId</c> and <c>CorrelationId</c> stay first-class instead of becoming substrings.
    /// </para>
    /// <para>
    /// Only four labels are attached — app, environment, level and (on search) variant. Labels
    /// define Loki streams, so a high-cardinality label such as user id creates a stream per user
    /// and degrades the whole instance. Everything identifying lives in the payload.
    /// </para>
    /// </summary>
    public static IHostBuilder UseApplicationLogging(this IHostBuilder host) =>
        host.UseSerilog((context, services, configuration) =>
        {
            var settings = context.Configuration.GetSettings<SerilogSettings>(SerilogSettings.SectionName);
            var environment = context.HostingEnvironment.EnvironmentName;

            var minimumLevel = Enum.TryParse<LogEventLevel>(settings.MinimumLevel, out var level)
                ? level
                : LogEventLevel.Information;

            configuration
                .MinimumLevel.Is(minimumLevel)
                // Framework logging is chatty enough at Information to bury application logs.
                // EF's Command category is dropped to Warning specifically: at Information it
                // logs every SQL statement, which in this application means embedding vectors.
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithEnvironmentName()
                .Enrich.WithThreadId()
                .Enrich.WithProperty("Application", settings.ApplicationName)
                .Enrich.WithProperty("Version", ThisAssembly.Version)
                // Redaction is enforced structurally rather than by convention: this policy
                // drops any property whose name looks credential-bearing, so a future logging
                // call cannot accidentally emit a token even if someone passes one.
                .Destructure.With(new SensitiveDataDestructuringPolicy())
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
                .WriteTo.GrafanaLoki(
                    settings.LokiUrl,
                    labels:
                    [
                        new LokiLabel { Key = "app", Value = settings.ApplicationName },
                        new LokiLabel { Key = "environment", Value = environment }
                    ],
                    propertiesAsLabels: ["level"],
                    restrictedToMinimumLevel: minimumLevel);
        });

    internal static class ThisAssembly
    {
        public static string Version { get; } =
            typeof(LoggingExtensions).Assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
