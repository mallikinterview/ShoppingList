using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;
using ShoppingList.Api.Common.Errors;
using ShoppingList.Api.Common.Extensions;
using ShoppingList.Api.Common.Middleware;
using ShoppingList.Api.Common.Validation;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Data;
using ShoppingList.Api.Features.Auth;
using ShoppingList.Api.Features.Items;
using ShoppingList.Api.Features.Search;
using ShoppingList.Api.Infrastructure.Caching;
using ShoppingList.Api.Infrastructure.Embeddings;
using ShoppingList.Api.Infrastructure.Identity;
using ShoppingList.Api.Infrastructure.Storage;

// ── Migration-only mode ──────────────────────────────────────────────────────────────
// Checked before anything is registered. The migrator compose service runs this and then
// exits, and the API waits on it completing.
//
// It builds its own minimal host rather than reusing the API's. Applying a schema change
// needs a database connection and nothing else, so requiring a reachable identity provider
// or a configured embedding model in order to migrate would be a coupling with no
// justification behind it. See MigrationHost for the full reasoning.
//
// Migrations are also never applied from the API startup path. With more than one replica
// every instance would race the same migration, and the losers crash-loop.
if (args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase))
{
    return await MigrationHost.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────────────
// Environment variables last so container configuration always wins over the committed
// appsettings files. The Section__Key convention means the same names work as env vars,
// in user-secrets, and in appsettings without any translation layer.
builder.Configuration.AddEnvironmentVariables();

builder.Host.UseApplicationLogging();

builder.Services.AddApplicationSettings(builder.Configuration);

// ── Infrastructure ───────────────────────────────────────────────────────────────────
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddIdentityAndAuthorization(builder.Configuration);
builder.Services.AddCaching(builder.Configuration);
builder.Services.AddObjectStorage(builder.Configuration);
builder.Services.AddEmbeddings(builder.Configuration);
builder.Services.AddSearch();
builder.Services.AddObservability(builder.Configuration, builder.Environment);
builder.Services.AddApplicationHealthChecks(builder.Configuration);

// ── API surface ──────────────────────────────────────────────────────────────────────
builder.Services.AddValidatorsFromApplicationAssembly();
builder.Services.AddApplicationRateLimiting(builder.Configuration);

// ProblemDetails for every failure, including the framework-generated 401/403/404/405 that
// would otherwise return an empty body and force clients to special-case them.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??=
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
        context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.GetCorrelationId();
    };
});

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddApplicationOpenApi();
builder.Services.AddResponseCompression(options => options.EnableForHttps = false);

// Behind a reverse proxy the original scheme and client IP arrive as headers. Without this
// the rate limiter partitions anonymous callers by the proxy's address — one bucket for
// everybody — and generated URLs come out as http.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// ── Pipeline ─────────────────────────────────────────────────────────────────────────
// Middleware order matters and is not arbitrary. Correlation runs first so every log line
// and error downstream carries the identifier. Authentication precedes authorization, which
// precedes rate limiting, so limits partition by a known user rather than by IP.
//
// Request logging sits OUTSIDE the exception handler, which is the opposite of the order most
// examples use and is deliberate. Serilog's request-logging middleware records status 500 for
// any request whose exception passes through it — see RequestLoggingMiddleware, which catches
// and logs with a hardcoded 500 before rethrowing. Placed inside the exception handler, it
// therefore observes the raw exception rather than the response the caller actually received,
// and every domain 404, 409 and 400 is written to Loki as "responded 500" with a full stack
// trace. The client gets the right status; only the log lies. That is the worst version of the
// bug: log-based error-rate alerting fires on ordinary client behaviour, and anyone reading the
// logs during an incident sees fabricated 500s. Outside the handler, the exception is already
// translated, so the logged status is the one that went over the wire.
app.UseForwardedHeaders();
app.UseCorrelationId();

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("CorrelationId", httpContext.GetCorrelationId());
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());

        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            diagnosticContext.Set("UserId", httpContext.User.GetSubjectId());
        }
    };

    // Health and metrics endpoints are polled every few seconds by Prometheus, Docker and
    // the orchestrator. At Information they drown out real traffic in Loki.
    options.GetLevel = (httpContext, _, exception) =>
    {
        if (exception is not null || httpContext.Response.StatusCode >= 500)
        {
            return Serilog.Events.LogEventLevel.Error;
        }

        var path = httpContext.Request.Path.Value ?? string.Empty;

        return path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase)
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
    };
});

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseResponseCompression();

app.UseAuthentication();
// Maps the token's `sub` claim to a local user row, provisioning on first sight. Placed
// after authentication and before authorization so downstream code can rely on a local
// user id existing for any authenticated request.
app.UseUserProvisioning();
app.UseAuthorization();
app.UseRateLimiter();

// ── Endpoints ────────────────────────────────────────────────────────────────────────
app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();

// Liveness deliberately checks nothing external: if a dependency outage failed the liveness
// probe, the orchestrator would restart healthy replicas and turn degradation into an outage.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    // Degraded still returns 200: the instance can serve traffic without its cache or its
    // embedder, and reporting it as unready would remove capacity for no reason.
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    },
    ResponseWriter = HealthResponseWriter.WriteAsync
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapItemEndpoints();
app.MapImageEndpoints();
app.MapSearchEndpoints();

// API exploration is not exposed outside development. In production it is an inventory of
// every route and schema handed to anyone who asks.
if (app.Environment.IsDevelopment())
{
    // Registered through a group so the anonymous convention reaches every endpoint Scalar
    // maps, not just the HTML page. A global FallbackPolicy requires an authenticated user
    // on anything that does not opt out, and Scalar serves its own JavaScript from separate
    // routes (/scalar/scalar.js, /scalar/scalar.aspnetcore.js). Allowing only the page left
    // those returning 401, so the shell loaded and the UI never booted — a blank screen with
    // a perfectly healthy API behind it.
    var docs = app.MapGroup(string.Empty).AllowAnonymous();

    docs.MapOpenApi();

    docs.MapScalarApiReference(options => options
        .WithTitle("Shopping List API")
        .WithTheme(ScalarTheme.BluePlanet)
        .EnableDarkMode()
        .WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl)
        .AddPreferredSecuritySchemes("Bearer")
        .EnablePersistentAuthentication());
}

try
{
    Log.Information("Starting {Application} in {Environment}",
        builder.Configuration["SerilogSettings:ApplicationName"], app.Environment.EnvironmentName);

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // Startup failures — a bad connection string, a failed options validation — otherwise
    // vanish into a container exit code with no explanation anywhere.
    Log.Fatal(ex, "Application terminated unexpectedly during startup");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Exposed so the integration test host can reference this entry point.</summary>
public partial class Program
{
    protected Program() { }
}
