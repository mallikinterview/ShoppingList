using NpgsqlTypes;
using Pgvector;

namespace ShoppingList.Api.Data.Entities;

public enum EmbeddingStatus
{
    Pending = 0,
    Ready = 1,
    Failed = 2
}

public sealed class ShoppingItem
{
    private ShoppingItem() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public AppUser User { get; private set; } = null!;

    public string Name { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    public int Quantity { get; private set; }

    public string? Unit { get; private set; }

    /// <summary>Metadata filter dimension for hybrid search.</summary>
    public string? Category { get; private set; }

    public bool IsPurchased { get; private set; }

    /// <summary>
    /// Stored generated column maintained by Postgres from Name and Notes. Generated rather than
    /// computed per query: a <c>to_tsvector(...)</c> in the WHERE clause cannot use a GIN index,
    /// so every search would sequentially scan the table.
    /// </summary>
    public NpgsqlTsVector SearchVector { get; private set; } = null!;

    /// <summary>
    /// Nullable because embeddings are generated in the background. An item is searchable by
    /// keyword the instant it is created, and gains vector recall when the worker catches up —
    /// rather than blocking the write for the duration of a model inference.
    /// </summary>
    public Vector? Embedding { get; private set; }

    public EmbeddingStatus EmbeddingStatus { get; private set; } = EmbeddingStatus.Pending;

    /// <summary>
    /// Which model produced <see cref="Embedding"/>. Recorded per row because embeddings from
    /// different models are not comparable: without this, a model upgrade silently corrupts
    /// similarity ranking across the corpus with no way to identify the stale rows.
    /// </summary>
    public string? EmbeddingModel { get; private set; }

    public DateTimeOffset? EmbeddedAt { get; private set; }

    public ICollection<ItemImage> Images { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Maps to Postgres <c>xmin</c>: a concurrency token that costs no schema and no write path
    /// maintenance. A lost update surfaces as <c>DbUpdateConcurrencyException</c> and becomes a
    /// 409, instead of one user's edit silently overwriting another's.
    /// </summary>
    public uint Version { get; private set; }

    public static ShoppingItem Create(
        Guid userId,
        string name,
        string? notes,
        int quantity,
        string? unit,
        string? category) => new()
        {
            // UUIDv7 is time-ordered, so inserts append to the end of the B-tree instead of
            // scattering across it the way UUIDv4 does. On a table that only grows, that is the
            // difference between sequential and random index writes.
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name.Trim(),
            Notes = notes?.Trim(),
            Quantity = quantity,
            Unit = unit?.Trim(),
            Category = category?.Trim(),
            IsPurchased = false,
            EmbeddingStatus = EmbeddingStatus.Pending
        };

    public void Update(string name, string? notes, int quantity, string? unit, string? category, bool isPurchased)
    {
        var embeddableChanged =
            !string.Equals(Name, name.Trim(), StringComparison.Ordinal)
            || !string.Equals(Notes, notes?.Trim(), StringComparison.Ordinal);

        Name = name.Trim();
        Notes = notes?.Trim();
        Quantity = quantity;
        Unit = unit?.Trim();
        Category = category?.Trim();
        IsPurchased = isPurchased;

        // Editing the text an embedding was derived from invalidates that embedding. Without
        // this the item keeps matching its old description forever — a stale-index bug that
        // produces plausible-looking but wrong results and is very hard to notice.
        if (embeddableChanged)
        {
            Embedding = null;
            EmbeddingStatus = EmbeddingStatus.Pending;
            EmbeddingModel = null;
            EmbeddedAt = null;
        }
    }

    public void SetEmbedding(Vector embedding, string model)
    {
        Embedding = embedding;
        EmbeddingModel = model;
        EmbeddingStatus = EmbeddingStatus.Ready;
        EmbeddedAt = DateTimeOffset.UtcNow;
    }

    public void MarkEmbeddingFailed()
    {
        EmbeddingStatus = EmbeddingStatus.Failed;
        EmbeddedAt = null;
    }

    /// <summary>Text handed to the embedding model. Kept here so index-time and query-time
    /// normalisation cannot drift apart across files.</summary>
    public string ToEmbeddingText() =>
        string.IsNullOrWhiteSpace(Notes) ? Name : $"{Name}. {Notes}";
}
