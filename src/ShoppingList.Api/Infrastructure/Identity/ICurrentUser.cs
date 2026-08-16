namespace ShoppingList.Api.Infrastructure.Identity;

/// <summary>
/// The authenticated caller, resolved once per request.
/// <para>
/// Exists so that ownership is enforced from a single source rather than from
/// <c>HttpContext.User</c> re-read in every handler. The EF global query filter reads
/// <see cref="UserId"/>, which is what makes cross-user access structurally impossible instead
/// of dependent on each handler remembering a check.
/// </para>
/// </summary>
public interface ICurrentUser
{
    /// <summary>Local database user id. Throws if unauthenticated — call sites are inside
    /// authorized endpoints, so a null here would be a bug, not a condition to branch on.</summary>
    Guid UserId { get; }

    /// <summary>OIDC subject claim.</summary>
    string SubjectId { get; }

    string? Username { get; }

    string? Email { get; }

    bool IsAuthenticated { get; }
}

internal sealed class CurrentUser : ICurrentUser
{
    private Guid? _userId;

    public Guid UserId => _userId
        ?? throw new InvalidOperationException(
            "Current user has not been resolved. This indicates an endpoint ran outside the " +
            "user-provisioning middleware, or was reached without authentication.");

    public string SubjectId { get; private set; } = string.Empty;

    public string? Username { get; private set; }

    public string? Email { get; private set; }

    public bool IsAuthenticated => _userId.HasValue;

    internal void Set(Guid userId, string subjectId, string? username, string? email)
    {
        _userId = userId;
        SubjectId = subjectId;
        Username = username;
        Email = email;
    }
}
