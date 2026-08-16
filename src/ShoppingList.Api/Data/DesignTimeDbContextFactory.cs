using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using ShoppingList.Api.Infrastructure.Identity;

namespace ShoppingList.Api.Data;

/// <summary>
/// Builds an <see cref="AppDbContext"/> for the EF Core command-line tools.
/// <para>
/// Without this, <c>dotnet ef</c> boots the full application host to locate the context — and
/// that host deliberately refuses to start without valid configuration, because
/// <c>ValidateOnStart</c> is exactly the behaviour we want at runtime. The result is that a
/// correct safety measure makes a routine developer command fail.
/// </para>
/// <para>
/// A design-time factory decouples the two. Scaffolding a migration needs the <em>model</em>, not
/// a running application and not a reachable database, so this constructs the minimum required
/// and nothing else. It also means <c>dotnet ef</c> works identically on a developer machine, in
/// CI, and inside a container, with no environment set up beforehand.
/// </para>
/// <para>
/// The connection string is honoured from the environment when present — so
/// <c>dotnet ef database update</c> can target a real database — and otherwise falls back to a
/// local default. No credential is committed here.
/// </para>
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=shoppinglist;Username=shoppinglist;Password=postgres";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("DatabaseSettings__ConnectionString")
            ?? FallbackConnectionString;

        // The data source, not a bare connection string: pgvector's type mapping is registered
        // on the source, and without it the migration would not know how to emit a vector column.
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSource, npgsql =>
            {
                npgsql.UseVector();
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
            })
            .Options;

        // Ownership is enforced by a global query filter reading ICurrentUser. Model building
        // only needs the expression tree, never the value — but the filter still has to resolve
        // to something, so design time gets a stub that yields an empty id rather than throwing.
        return new AppDbContext(options, new DesignTimeCurrentUser());
    }

    private sealed class DesignTimeCurrentUser : ICurrentUser
    {
        public Guid UserId => Guid.Empty;

        public string SubjectId => string.Empty;

        public string? Username => null;

        public string? Email => null;

        public bool IsAuthenticated => false;
    }
}
