using System.Diagnostics.Metrics;

namespace ShoppingList.Tests.Unit;

/// <summary>
/// Minimal <see cref="IMeterFactory"/> for tests.
/// <para>
/// Hand-written rather than pulling in Microsoft.Extensions.Diagnostics.Testing: the only thing
/// needed here is a meter that can be created and disposed. Adding a package to obtain fifteen
/// lines is a dependency that has to be versioned, audited and explained for the rest of the
/// project's life.
/// </para>
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }

        _meters.Clear();
    }
}
