using Serilog.Context;

namespace ShoppingList.Api.Common.Middleware;

/// <summary>
/// Establishes one identifier that ties together every log line, error response and downstream
/// call belonging to a single request.
/// <para>
/// An inbound <c>X-Correlation-Id</c> is honoured so a caller's identifier survives the hop —
/// that is what makes the header useful across service boundaries rather than only within this
/// process. It is length-capped and filtered before use: the value ends up in log output and in
/// a response header, so an unbounded or newline-bearing string is a log-injection vector.
/// </para>
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    internal const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[ItemKey] = correlationId;

        // Written before the response starts, or it is lost on any response that begins
        // streaming — which includes every error path that writes a body.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        // Pushed onto Serilog's LogContext so every log written during this request carries it,
        // including logs from framework and library code that knows nothing about correlation.
        using (LogContext.PushProperty(ItemKey, correlationId))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var supplied))
        {
            return Guid.NewGuid().ToString("N");
        }

        var candidate = supplied.ToString();

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxLength)
        {
            return Guid.NewGuid().ToString("N");
        }

        // Anything outside this set could forge a log line or split a response header.
        foreach (var c in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return Guid.NewGuid().ToString("N");
            }
        }

        return candidate;
    }
}

public static class CorrelationIdExtensions
{
    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue("CorrelationId", out var value) && value is string id
            ? id
            : string.Empty;

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
