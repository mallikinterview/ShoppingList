using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ShoppingList.Api.Common.Middleware;

namespace ShoppingList.Api.Common.Errors;

/// <summary>
/// Single exit point for unhandled exceptions, producing RFC 9457 <c>application/problem+json</c>
/// for every failure — including the ones people usually leave as bare status codes.
/// <para>
/// Implemented as <see cref="IExceptionHandler"/> rather than a try/catch middleware: the pipeline
/// invokes it in the right place relative to the rest of the framework's error handling, and it
/// composes with <c>AddProblemDetails()</c> instead of competing with it.
/// </para>
/// <para>
/// Detail text is never taken from the exception for unexpected errors. An unhandled exception's
/// message routinely contains connection strings, SQL fragments or file paths, and returning it to
/// the caller is an information leak that is trivially easy to ship by accident.
/// </para>
/// </summary>
internal sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // A cancelled request is the client hanging up, not a server failure. Logging it as an
        // error trains people to ignore the error log.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request {Method} {Path} was cancelled by the client.",
                httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return true;
        }

        var (statusCode, title, detail) = Describe(exception);

        // Expected failures are informational; anything else is a defect and gets a full stack.
        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation("Request failed with {StatusCode} on {Method} {Path}: {Reason}",
                statusCode, httpContext.Request.Method, httpContext.Request.Path, exception.Message);
        }

        httpContext.Response.StatusCode = statusCode;

        // A 503 without Retry-After tells the caller to try again but not when, so considerate
        // clients guess and inconsiderate ones hammer a dependency that is already struggling.
        if (statusCode == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "5";
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail,
                Type = $"https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/{statusCode}",
                Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}",
                Extensions =
                {
                    // Echoed so a caller reporting a problem can hand over one value that finds
                    // every log line for the request in Loki.
                    ["correlationId"] = httpContext.GetCorrelationId()
                }
            }
        });
    }

    private static (int StatusCode, string Title, string Detail) Describe(Exception exception) => exception switch
    {
        AppException app => (app.StatusCode, app.Title, app.Message),

        // Not surfaced as 400: reaching here means an argument check that should have been a
        // validation rule was missed. That is a defect in our code, not bad input.
        ArgumentException => (StatusCodes.Status500InternalServerError, "An unexpected error occurred",
            "The request could not be processed."),

        // Both the framework's timeout and the one our own resilience pipeline enforces. They
        // are unrelated types — Polly's derives from ExecutionRejectedException, not from
        // TimeoutException — so handling only the first leaves every policy-enforced timeout
        // falling through to 500.
        TimeoutException or TimeoutRejectedException => (StatusCodes.Status504GatewayTimeout,
            "Upstream timeout", "A dependency did not respond in time. Please retry."),

        // The circuit breaker refusing the call, before it is ever attempted.
        //
        // Worth stating plainly because it is counter-intuitive: the breaker exists to protect
        // this service from a failing dependency, and without this case it would have made
        // things worse. Once open it throws BrokenCircuitException instead of calling out, and
        // an unrecognised exception is a 500 — so the precise moment the protection engages is
        // the moment every response turns into "this service is broken". An open breaker is the
        // clearest possible statement that a dependency is unavailable and the caller should
        // come back shortly, which is 503 by definition.
        BrokenCircuitException => (StatusCodes.Status503ServiceUnavailable, "Service unavailable",
            "A required service is temporarily unavailable. Please retry."),

        // Backstop for any outbound call that was not wrapped at its client. Each client should
        // still translate its own failures, because only the client knows which dependency it
        // was talking to and can say so in the message — but a missed wrapping should degrade to
        // "a dependency is unreachable", not to "this service is broken". The distinction is the
        // difference between a caller retrying and a caller giving up, and between an alert that
        // pages someone and one that does not.
        HttpRequestException => (StatusCodes.Status503ServiceUnavailable, "Service unavailable",
            "A required service is temporarily unreachable. Please retry."),

        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred",
            "The request could not be processed. If the problem persists, quote the correlation ID.")
    };
}
