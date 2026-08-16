using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Infrastructure.Identity;
using ShoppingList.Api.Telemetry;

namespace ShoppingList.Api.Common.RateLimiting;

public static class RateLimitPolicies
{
    public const string Standard = "standard";
    public const string Auth = "auth";
    public const string Upload = "upload";
}

public static class RateLimitingExtensions
{
    public static IServiceCollection AddApplicationRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = configuration.GetSettings<RateLimitSettings>(RateLimitSettings.SectionName);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(RateLimitPolicies.Standard, context =>
                CreateLimiter(context, settings.PermitLimit, settings.WindowSeconds));

            // Stricter, because these endpoints are the credential-stuffing surface. Keycloak's
            // own brute-force protection locks out a single account under repeated failures; it
            // does nothing about one attacker trying one common password against a thousand
            // different accounts, which is what this limit is for.
            options.AddPolicy(RateLimitPolicies.Auth, context =>
                CreateLimiter(context, settings.AuthPermitLimit, settings.AuthWindowSeconds));

            // Separate again: uploads consume bandwidth and storage rather than CPU, so the
            // sensible limit is a different number for a different reason.
            options.AddPolicy(RateLimitPolicies.Upload, context =>
                CreateLimiter(context, settings.UploadPermitLimit, settings.UploadWindowSeconds));

            options.OnRejected = async (context, cancellationToken) =>
            {
                var metrics = context.HttpContext.RequestServices.GetRequiredService<ApiMetrics>();
                var policy = context.HttpContext.GetEndpoint()?.DisplayName ?? "unknown";
                metrics.RecordRateLimitRejection(policy);

                // Retry-After turns a 429 from a dead end into something a client can handle
                // correctly. Without it, well-behaved clients guess and badly-behaved ones
                // hammer — which is the traffic the limit was trying to shed.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/problem+json";

                // Same problem+json envelope as every other error. A 429 with an empty body
                // forces clients to special-case the one response they are most likely to hit.
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    type = "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/429",
                    title = "Too many requests",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = "Rate limit exceeded. Retry after the interval indicated by the Retry-After header.",
                    correlationId = context.HttpContext.GetCorrelationIdSafe()
                }, cancellationToken);
            };
        });

        return services;
    }

    /// <summary>
    /// Partitions by authenticated user, falling back to client IP.
    /// <para>
    /// Partitioning by IP alone would put every user behind one corporate NAT or mobile carrier
    /// into a single bucket, so one heavy user throttles an entire office. Partitioning by user
    /// where a user exists makes the limit mean what it says.
    /// </para>
    /// <para>
    /// A sliding window rather than a fixed one: a fixed window lets a caller spend the whole
    /// allowance at the end of one window and again at the start of the next, briefly doubling
    /// the intended rate at exactly the moment the limit matters.
    /// </para>
    /// </summary>
    private static RateLimitPartition<string> CreateLimiter(HttpContext context, int permitLimit, int windowSeconds)
    {
        var partitionKey = context.User.Identity?.IsAuthenticated == true
            ? $"user:{context.User.GetSubjectId()}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            SegmentsPerWindow = 4,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            // Zero queue depth: queueing a request that is over the limit holds a connection
            // open to eventually reject it anyway. Rejecting immediately sheds the load, which
            // is the entire purpose.
            QueueLimit = 0
        });
    }

    private static string GetCorrelationIdSafe(this HttpContext context) =>
        context.Items.TryGetValue("CorrelationId", out var value) && value is string id ? id : string.Empty;
}
