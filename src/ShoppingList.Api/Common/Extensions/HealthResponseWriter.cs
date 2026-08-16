using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShoppingList.Api.Common.Middleware;

namespace ShoppingList.Api.Common.Extensions;

/// <summary>
/// Writes health results as JSON with per-check detail.
/// <para>
/// The framework default writes the single word "Healthy". That is enough for an orchestrator
/// and useless for a human: when readiness fails at 3am, the difference between "Postgres is
/// unreachable" and "Ollama is slow" is the difference between a five-minute fix and an hour of
/// guessing. Exception messages are excluded — the endpoint is unauthenticated, and a connection
/// failure message routinely contains the connection string.
/// </para>
/// </summary>
internal static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            correlationId = context.GetCorrelationId(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                description = entry.Value.Description,
                tags = entry.Value.Tags
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, SerializerOptions));
    }
}
