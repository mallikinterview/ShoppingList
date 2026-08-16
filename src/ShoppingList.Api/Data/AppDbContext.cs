using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ShoppingList.Api.Data.Entities;
using ShoppingList.Api.Infrastructure.Identity;

namespace ShoppingList.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<ShoppingItem> Items => Set<ShoppingItem>();

    public DbSet<ItemImage> Images => Set<ItemImage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // ── Ownership, enforced in SQL ────────────────────────────────────────────────
        //
        // This is the single most important line in the data layer. Every query against Items
        // and Images — including ones written years from now by someone who has never read this
        // comment — has `WHERE user_id = @currentUser` appended by the provider.
        //
        // The alternative, checking `item.UserId != currentUser.UserId` after loading, is one
        // forgotten branch away from a cross-account data leak, and that branch is forgotten on
        // the endpoint nobody thinks about: an export, a bulk operation, a search. Here the leak
        // is not possible to write by accident; bypassing it requires typing IgnoreQueryFilters,
        // which is greppable and shows up in review.
        //
        // The filter reads a scoped service rather than a captured value, so it resolves per
        // request rather than being baked into the compiled model at startup.
        modelBuilder.Entity<ShoppingItem>()
            .HasQueryFilter(item => item.UserId == currentUser.UserId);

        modelBuilder.Entity<ItemImage>()
            .HasQueryFilter(image => image.Item.UserId == currentUser.UserId);

        ApplySnakeCaseNames(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Renames every table, column, key, foreign key and index to snake_case.
    /// <para>
    /// Postgres folds unquoted identifiers to lower case, so a PascalCase column has to be quoted
    /// in every hand-written statement forever — and the hybrid search query is hand-written by
    /// necessity. Matching the database's own convention means the SQL reads the way a Postgres
    /// developer expects and needs no quoting at all.
    /// </para>
    /// <para>
    /// Applied as a convention rather than forty <c>HasColumnName</c> calls: a per-property
    /// mapping is something a future property can silently be added without, at which point the
    /// raw SQL breaks at runtime rather than at compile time. Done here, a new property cannot
    /// get it wrong.
    /// </para>
    /// <para>
    /// Deliberately not a package (EFCore.NamingConventions) — twenty lines against a dependency
    /// to version, audit and keep aligned with the EF major version is not a trade worth making.
    /// </para>
    /// </summary>
    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var tableName = entity.GetTableName();
            if (tableName is not null)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            var storeObject = StoreObjectIdentifier.Table(
                entity.GetTableName() ?? entity.ClrType.Name,
                entity.GetSchema());

            foreach (var property in entity.GetProperties())
            {
                var columnName = property.GetColumnName(storeObject) ?? property.Name;
                property.SetColumnName(ToSnakeCase(columnName));
            }

            foreach (var key in entity.GetKeys())
            {
                var name = key.GetName();
                if (name is not null)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var name = foreignKey.GetConstraintName();
                if (name is not null)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                var name = index.GetDatabaseName();
                if (name is not null)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    /// <summary>
    /// Idempotent: an identifier already in snake_case is returned unchanged, so explicitly
    /// configured names such as <c>shopping_items</c> and <c>ix_shopping_items_user_created</c>
    /// pass through untouched.
    /// </summary>
    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new System.Text.StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];

            if (char.IsUpper(current))
            {
                // An underscore is inserted only at a genuine word boundary, so 'SearchVector'
                // becomes search_vector while an acronym such as 'URLPath' does not become
                // u_r_l_path.
                var previousIsLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);

                if (i > 0 && name[i - 1] != '_' && (previousIsLowerOrDigit || nextIsLower))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Everything is timestamptz. Postgres stores it as UTC and returns it with an offset,
        // which removes the entire category of "is this local or UTC" bugs at the boundary.
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<string>().HaveMaxLength(512);

        base.ConfigureConventions(configurationBuilder);
    }
}
