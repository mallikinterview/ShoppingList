using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Telemetry;

namespace ShoppingList.Api.Experimentation;

public sealed record VariantAssignment(string Variant, string Strategy)
{
    public const string Control = "control";
    public const string Treatment = "treatment";
    public const string Off = "off";
}

public interface IVariantAssigner
{
    VariantAssignment Assign(Guid userId);
}

/// <summary>
/// Deterministic per-user assignment to a ranking variant.
/// <para>
/// Three properties matter, and all three are things that are easy to get wrong and expensive
/// to discover afterwards:
/// </para>
/// <list type="number">
/// <item><b>Stickiness.</b> A user must get the same variant on every request, forever. Random
/// per-request assignment produces search results that reorder between page loads and makes any
/// measured difference meaningless — the same user contributes to both arms.</item>
/// <item><b>Uniformity.</b> Hashing a GUID by taking its low bits, or using
/// <see cref="string.GetHashCode()"/>, does not distribute evenly and is not stable across
/// processes. A cryptographic hash gives a uniform, reproducible bucket.</item>
/// <item><b>Independence between experiments.</b> The experiment key salts the hash, so a user
/// bucketed into treatment here is not correlated with their bucket in the next experiment.
/// Without the salt every experiment tests the same half of the population.</item>
/// </list>
/// <para>
/// Not included, deliberately: experiment definitions, exposure event logging, and significance
/// testing. This is the assignment primitive and the integration point, not an experimentation
/// platform.
/// </para>
/// </summary>
internal sealed class VariantAssigner(IOptions<SearchSettings> options, ApiMetrics metrics) : IVariantAssigner
{
    private readonly SearchSettings _settings = options.Value;

    public VariantAssignment Assign(Guid userId)
    {
        if (!_settings.Experiment.Enabled)
        {
            // "off" rather than "control": a user served by the default configuration is not in
            // the experiment at all, and folding them into the control arm would contaminate it.
            // It is also a distinct cache-key namespace, so results from before an experiment
            // started are never served into it.
            return new VariantAssignment(VariantAssignment.Off, _settings.Strategy);
        }

        var bucket = Bucket(userId, _settings.Experiment.Key);

        var assignment = bucket < _settings.Experiment.VariantSplit
            ? new VariantAssignment(VariantAssignment.Treatment, _settings.Experiment.TreatmentStrategy)
            : new VariantAssignment(VariantAssignment.Control, _settings.Experiment.ControlStrategy);

        metrics.RecordAssignment(assignment.Variant);

        return assignment;
    }

    /// <summary>
    /// Maps (user, experiment) to a stable bucket in [0, 100).
    /// <para>
    /// SHA-256 is used for its distribution, not for secrecy — assignment is not a secret. What
    /// it buys over a cheaper hash is a guarantee that the output is uniform and identical across
    /// processes, restarts and framework versions, which
    /// <see cref="string.GetHashCode()"/> explicitly does not provide (it is randomised per
    /// process, so every restart would reassign every user).
    /// </para>
    /// </summary>
    internal static int Bucket(Guid userId, string experimentKey)
    {
        var input = Encoding.UTF8.GetBytes($"{experimentKey}:{userId:D}");
        var hash = SHA256.HashData(input);

        // First four bytes, big-endian, masked to positive.
        var value = ((hash[0] & 0x7F) << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3];

        return value % 100;
    }
}
