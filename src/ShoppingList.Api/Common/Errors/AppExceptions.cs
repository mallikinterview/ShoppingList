namespace ShoppingList.Api.Common.Errors;

/// <summary>
/// Base for expected failures that map to a specific HTTP status.
/// <para>
/// These exist so handlers can fail in one line without every caller having to thread a result
/// type through. They are for genuinely exceptional-but-expected conditions only — not for
/// control flow, and never for validation, which is rejected at the endpoint filter before a
/// handler is reached.
/// </para>
/// </summary>
public abstract class AppException(string message, string title) : Exception(message)
{
    public abstract int StatusCode { get; }

    public string Title { get; } = title;
}

public sealed class NotFoundException(string message = "The requested resource was not found.")
    : AppException(message, "Resource not found")
{
    public override int StatusCode => StatusCodes.Status404NotFound;
}

/// <summary>
/// Deliberately rare in this codebase. Cross-user access returns <see cref="NotFoundException"/>
/// rather than 403, because a 403 confirms that the resource exists and belongs to somebody else
/// — an enumeration oracle. 403 is reserved for cases where the caller already knows the resource
/// exists and simply lacks a role.
/// </summary>
public sealed class ForbiddenException(string message = "You do not have access to this resource.")
    : AppException(message, "Forbidden")
{
    public override int StatusCode => StatusCodes.Status403Forbidden;
}

public sealed class ConflictException(string message)
    : AppException(message, "Conflict")
{
    public override int StatusCode => StatusCodes.Status409Conflict;
}

/// <summary>Input that passed schema validation but violates a rule requiring state to evaluate.</summary>
public sealed class BadRequestException(string message)
    : AppException(message, "Invalid request")
{
    public override int StatusCode => StatusCodes.Status400BadRequest;
}

/// <summary>
/// A downstream dependency is unavailable and the request cannot be served in a degraded form.
/// Search does not throw this — it falls back to keyword-only ranking instead.
/// </summary>
public sealed class DependencyUnavailableException(string dependency, Exception? inner = null)
    : AppException($"The '{dependency}' service is currently unavailable.", "Service unavailable")
{
    public override int StatusCode => StatusCodes.Status503ServiceUnavailable;

    public string Dependency { get; } = dependency;

    public Exception? Inner { get; } = inner;
}
