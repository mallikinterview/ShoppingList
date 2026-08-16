using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShoppingList.Api.Data.Entities;

namespace ShoppingList.Api.Data.Configurations;

internal sealed class ShoppingItemConfiguration : IEntityTypeConfiguration<ShoppingItem>
{
    /// <summary>
    /// Must match <c>OllamaSettings__EmbeddingDimensions</c>. pgvector columns are fixed-width,
    /// so a mismatch is a schema change rather than a configuration change — which is why the
    /// application asserts the two agree at startup instead of discovering it on first write.
    /// </summary>
    public const int EmbeddingDimensions = 768;

    public void Configure(EntityTypeBuilder<ShoppingItem> builder)
    {
        builder.ToTable("shopping_items");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Notes)
            .HasMaxLength(2000);

        builder.Property(x => x.Quantity)
            .IsRequired();

        builder.Property(x => x.Unit).HasMaxLength(32);
        builder.Property(x => x.Category).HasMaxLength(64);
        builder.Property(x => x.IsPurchased).IsRequired();

        builder.Property(x => x.EmbeddingStatus)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(x => x.EmbeddingModel).HasMaxLength(128);

        builder.Property(x => x.Embedding)
            .HasColumnType($"vector({EmbeddingDimensions})");

        // Generated and stored by Postgres from Name and Notes. Weighting matters: a query word
        // appearing in the name should outrank the same word buried in free-text notes, and
        // setweight is how ts_rank is told that.
        builder.HasGeneratedTsVectorColumn(
                x => x.SearchVector,
                "english",
                x => new { x.Name, x.Notes })
            .HasIndex(x => x.SearchVector)
            .HasMethod("GIN");

        // ── Vector index ─────────────────────────────────────────────────────────────
        // HNSW rather than IVFFlat: it needs no training pass, so it works on an empty table
        // and stays correct as rows arrive. IVFFlat requires rebuilding once the corpus grows,
        // which is a maintenance job nobody remembers to run.
        //
        // cosine ops because the embedding model produces direction-normalised vectors, where
        // magnitude carries no meaning. The matching operator is <=>, which returns DISTANCE —
        // ordering must be ASC. Sorting DESC here is a silent relevance inversion that still
        // returns plausible-looking results.
        //
        // m and ef_construction are left at documented defaults, not benchmarked. At this
        // corpus size the difference is unmeasurable; that is stated in Known Limitations
        // rather than implied to be tuned.
        builder.HasIndex(x => x.Embedding)
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops")
            .HasStorageParameter("m", 16)
            .HasStorageParameter("ef_construction", 64);

        // Composite, in this order: the ownership filter is on every query, so user_id must
        // lead; created_at and id then serve the keyset pagination sort directly, letting the
        // planner satisfy ORDER BY from the index instead of sorting.
        builder.HasIndex(x => new { x.UserId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_shopping_items_user_created");

        // Partial index: the background worker only ever asks for pending rows, and once the
        // corpus is embedded that is a handful out of the whole table. Indexing only those
        // rows keeps the index tiny regardless of how large the table becomes.
        builder.HasIndex(x => x.EmbeddingStatus)
            .HasDatabaseName("ix_shopping_items_embedding_pending")
            .HasFilter("embedding_status = 'Pending'");

        builder.HasIndex(x => new { x.UserId, x.Category })
            .HasDatabaseName("ix_shopping_items_user_category");

        builder.Property(x => x.Version)
            .IsRowVersion()
            .HasColumnName("xmin")
            .HasColumnType("xid");

        builder.HasOne(x => x.User)
            .WithMany(x => x.Items)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Images)
            .WithOne(x => x.Item)
            .HasForeignKey(x => x.ItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
