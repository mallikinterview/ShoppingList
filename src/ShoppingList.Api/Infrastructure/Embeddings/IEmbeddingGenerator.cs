using Pgvector;

namespace ShoppingList.Api.Infrastructure.Embeddings;

/// <summary>
/// One of only three infrastructure interfaces in this codebase. It earns its place: the
/// embedder has a real alternative implementation (OpenAI, Cohere, a local ONNX model), a real
/// failure mode that the search path must handle, and it must be stubbed in tests so ranking
/// assertions are deterministic rather than dependent on a live model.
/// </summary>
public interface IEmbeddingGenerator
{
    /// <summary>Returns null when the embedding could not be produced. Failure is a normal
    /// outcome here, not an exception: search degrades to keyword-only rather than erroring.</summary>
    Task<Vector?> GenerateAsync(string text, CancellationToken ct);

    string ModelName { get; }

    int Dimensions { get; }
}
