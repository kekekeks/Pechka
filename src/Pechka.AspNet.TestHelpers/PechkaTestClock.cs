using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// The harness's calendar clock, registered as the <see cref="TimeProvider"/> on every
/// harness-started host unless the app's composition supplies its own provider. It tracks real
/// UTC (flowing, never frozen) plus an appendable offset — forward time travel only; backward
/// travel throws.
///
/// <para>Advancing a shared lane's clock is safe: a lease is exclusive ownership of the host and
/// database, and a forward offset is indistinguishable from running the suite later in real
/// time — which isolation-by-uniqueness already requires every test to survive. Timers and
/// delays are unaffected (the clock does not override them); it moves calendar reads only.</para>
/// </summary>
public sealed class PechkaTestClock : TimeProvider
{
    private long _offsetTicks;

    /// <summary>The accumulated forward offset.</summary>
    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;

    /// <summary>Appends to the offset. Forward only.</summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(by), by, "Backward time travel is not supported");
        Interlocked.Add(ref _offsetTicks, by.Ticks);
    }
}

public static class PechkaTestClockServiceCollectionExtensions
{
    /// <summary>
    /// Makes the given clock the host's <see cref="TimeProvider"/>, replacing whatever is
    /// registered. Use from a per-host harness delegate when hosts must share one clock instance
    /// (e.g. two hosts on one database) or start pre-advanced; ordinary hosts get their own
    /// <see cref="PechkaTestClock"/> automatically.
    /// </summary>
    public static IServiceCollection UseTestClock(this IServiceCollection services, PechkaTestClock clock)
    {
        services.RemoveAll<TimeProvider>();
        services.RemoveAll<PechkaTestClock>();
        services.AddSingleton(clock);
        services.AddSingleton<TimeProvider>(clock);
        return services;
    }
}
