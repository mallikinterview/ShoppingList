using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ShoppingList.Api.Data.Interceptors;

/// <summary>
/// Stamps CreatedAt and UpdatedAt on every insert and update.
/// <para>
/// An interceptor rather than a base-class hook or per-handler assignment: it cannot be
/// forgotten, it applies to bulk operations and to entities added by code that never thought
/// about auditing, and it keeps timestamp logic out of the domain entities entirely.
/// </para>
/// <para>
/// <see cref="TimeProvider"/> rather than <c>DateTimeOffset.UtcNow</c> so tests can control the
/// clock. Untestable time is one of the most common sources of flaky tests, and it costs nothing
/// to avoid.
/// </para>
/// </summary>
internal sealed class AuditingInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (entry.Metadata.FindProperty("UpdatedAt") is not null)
            {
                entry.Property("UpdatedAt").CurrentValue = now;
            }

            if (entry.State == EntityState.Added && entry.Metadata.FindProperty("CreatedAt") is not null)
            {
                entry.Property("CreatedAt").CurrentValue = now;
            }
        }
    }
}
