namespace ShoppingList.Api.Data.Entities;

/// <summary>
/// Local projection of a Keycloak user.
/// <para>
/// Keycloak owns identity; this row exists so shopping items have a stable foreign key and so
/// ownership can be enforced in SQL rather than by comparing strings in application code. It
/// holds no credential — there is no password hash, no salt and no token here.
/// </para>
/// </summary>
public sealed class AppUser
{
    private AppUser() { }

    public Guid Id { get; private set; }

    /// <summary>
    /// The OIDC <c>sub</c> claim. Unique, and the only field ever used to look a user up.
    /// Username and email are display data: both are mutable in Keycloak, and matching on
    /// either would let a renamed account inherit another user's items.
    /// </summary>
    public string SubjectId { get; private set; } = string.Empty;

    public string? Username { get; private set; }

    public string? Email { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public ICollection<ShoppingItem> Items { get; private set; } = [];

    public static AppUser Create(string subjectId, string? username, string? email) => new()
    {
        Id = Guid.CreateVersion7(),
        SubjectId = subjectId,
        Username = username,
        Email = email
    };

    public bool HasProfileChanged(string? username, string? email) =>
        !string.Equals(Username, username, StringComparison.Ordinal)
        || !string.Equals(Email, email, StringComparison.Ordinal);

    public void UpdateProfile(string? username, string? email)
    {
        Username = username;
        Email = email;
    }
}
