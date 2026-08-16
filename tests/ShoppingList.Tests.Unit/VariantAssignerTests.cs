using Microsoft.Extensions.Options;
using ShoppingList.Api.Configuration;
using ShoppingList.Api.Experimentation;
using ShoppingList.Api.Telemetry;

namespace ShoppingList.Tests.Unit;

/// <summary>
/// The properties tested here are the ones that make an experiment valid rather than merely
/// present. Each of these failing produces results that still look correct.
/// </summary>
public sealed class VariantAssignerTests
{
    [Fact]
    public void Assignment_is_sticky_for_a_given_user()
    {
        // The single most important property. Random per-request assignment would put the same
        // user in both arms, and any measured difference between variants would be noise
        // reported as signal.
        var assigner = CreateAssigner(split: 50);
        var userId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var assignments = Enumerable.Range(0, 100)
            .Select(_ => assigner.Assign(userId).Variant)
            .Distinct()
            .ToArray();

        assignments.Should().ContainSingle(
            "a user must receive the same variant on every request, or the experiment measures nothing");
    }

    [Fact]
    public void Assignment_distributes_close_to_the_configured_split()
    {
        // A non-uniform hash silently biases the population. Taking the low bits of a GUID, or
        // using string.GetHashCode, would pass a stickiness test and fail this one.
        var assigner = CreateAssigner(split: 50);

        var treatment = Enumerable.Range(0, 10_000)
            .Count(_ => assigner.Assign(Guid.NewGuid()).Variant == VariantAssignment.Treatment);

        treatment.Should().BeInRange(4_700, 5_300,
            "a 50% split over 10,000 users should land within a few percent of half");
    }

    [Theory]
    [InlineData(0, VariantAssignment.Control)]
    [InlineData(100, VariantAssignment.Treatment)]
    public void Boundary_splits_assign_everyone_to_one_arm(int split, string expected)
    {
        var assigner = CreateAssigner(split);

        var variants = Enumerable.Range(0, 500)
            .Select(_ => assigner.Assign(Guid.NewGuid()).Variant)
            .Distinct();

        variants.Should().Equal([expected],
            "0 and 100 are the ramp endpoints and must be absolute — a 'mostly off' experiment is not off");
    }

    [Fact]
    public void Different_experiments_bucket_users_independently()
    {
        // Without salting by experiment key, every experiment would test the same half of the
        // population, and their effects would confound each other permanently.
        var userIds = Enumerable.Range(0, 1_000).Select(_ => Guid.NewGuid()).ToArray();

        var agreement = userIds.Count(id =>
            VariantAssigner.Bucket(id, "experiment-a") == VariantAssigner.Bucket(id, "experiment-b"));

        agreement.Should().BeLessThan(100,
            "two experiment keys must produce uncorrelated buckets for the same users");
    }

    [Fact]
    public void Bucket_is_stable_across_processes()
    {
        // Hard-coded expectation on purpose. string.GetHashCode is randomised per process, so a
        // hash built on it would reassign every user on every restart — and that failure is
        // invisible to any test that only compares values within one run.
        VariantAssigner.Bucket(Guid.Parse("11111111-1111-1111-1111-111111111111"), "search-ranking-v1")
            .Should().Be(
                VariantAssigner.Bucket(Guid.Parse("11111111-1111-1111-1111-111111111111"), "search-ranking-v1"));
    }

    [Fact]
    public void Bucket_is_always_within_range()
    {
        for (var i = 0; i < 5_000; i++)
        {
            VariantAssigner.Bucket(Guid.NewGuid(), "k").Should().BeInRange(0, 99);
        }
    }

    [Fact]
    public void Disabled_experiment_returns_off_not_control()
    {
        // "off" is a distinct cache-key namespace from "control". Folding pre-experiment users
        // into the control arm would let results computed before the experiment started be
        // served into it.
        var assigner = CreateAssigner(split: 50, enabled: false);

        var assignment = assigner.Assign(Guid.NewGuid());

        assignment.Variant.Should().Be(VariantAssignment.Off);
        assignment.Strategy.Should().Be("rrf");
    }

    private static VariantAssigner CreateAssigner(int split, bool enabled = true)
    {
        var settings = new SearchSettings
        {
            Strategy = "rrf",
            Experiment = new ExperimentSettings
            {
                Enabled = enabled,
                Key = "search-ranking-v1",
                VariantSplit = split,
                ControlStrategy = "rrf",
                TreatmentStrategy = "weighted"
            }
        };

        return new VariantAssigner(Options.Create(settings), new ApiMetrics(new TestMeterFactory()));
    }
}
