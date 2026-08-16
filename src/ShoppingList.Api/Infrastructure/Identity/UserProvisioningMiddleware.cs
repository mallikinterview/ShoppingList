using Microsoft.EntityFrameworkCore;
using Serilog.Context;
using ShoppingList.Api.Data;
using ShoppingList.Api.Data.Entities;

namespace ShoppingList.Api.Infrastructure.Identity;

/// <summary>
/// Maps the token's <c>sub</c> claim to a local user row, creating it on first sight.
/// <para>
/// Just-in-time provisioning rather than a signup-time write, because identity lives in Keycloak
/// and users can arrive by paths this API never sees — the admin console, a future federated
/// identity provider, a direct realm import. Any of those would otherwise produce a valid token
/// for a user with no local row, and every subsequent request would fail on a foreign key.
/// </para>
/// <para>
/// Runs after authentication and before authorization, so anything downstream — handlers, the EF
/// global query filter — can rely on a local user id existing for every authenticated request.
/// </para>
/// </summary>
internal sealed class UserProvisioningMiddleware(RequestDelegate next, ILogger<UserProvisioningMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AppDbContext dbContext, ICurrentUser currentUser)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var subjectId = context.User.GetSubjectId();
        var username = context.User.GetPreferredUsername();
        var email = context.User.GetEmail();

        // IgnoreQueryFilters: the users table is filtered by the current user, and the current
        // user is precisely what this middleware is resolving. Without it the lookup filters on
        // an identity that does not exist yet and always misses.
        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.SubjectId == subjectId, context.RequestAborted);

        if (user is null)
        {
            user = AppUser.Create(subjectId, username, email);
            dbContext.Users.Add(user);

            try
            {
                await dbContext.SaveChangesAsync(context.RequestAborted);
                logger.LogInformation("Provisioned local user {UserId} for subject {SubjectId}",
                    user.Id, subjectId);
            }
            catch (DbUpdateException ex)
            {
                // Two concurrent first requests from the same new user both miss the read and
                // both insert. The unique index on SubjectId makes one of them lose; that loser
                // re-reads rather than failing the request. Detaching first prevents the failed
                // insert from being retried on the next SaveChanges in this scope.
                dbContext.Entry(user).State = EntityState.Detached;

                user = await dbContext.Users
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(u => u.SubjectId == subjectId, context.RequestAborted);

                if (user is null)
                {
                    throw;
                }

                logger.LogDebug(ex, "Lost provisioning race for subject {SubjectId}; using existing row {UserId}",
                    subjectId, user.Id);
            }
        }
        else if (user.HasProfileChanged(username, email))
        {
            // Keycloak stays the source of truth; the local copy is a cache for display and is
            // refreshed opportunistically rather than by a synchronisation job.
            user.UpdateProfile(username, email);
            await dbContext.SaveChangesAsync(context.RequestAborted);
        }

        ((CurrentUser)currentUser).Set(user.Id, subjectId, username, email);

        // Structured property, not a Loki label — one stream per user would be a cardinality
        // disaster, while a payload field stays fully queryable.
        using (LogContext.PushProperty("UserId", user.Id))
        {
            await next(context);
        }
    }
}

public static class UserProvisioningExtensions
{
    public static IApplicationBuilder UseUserProvisioning(this IApplicationBuilder app) =>
        app.UseMiddleware<UserProvisioningMiddleware>();
}
