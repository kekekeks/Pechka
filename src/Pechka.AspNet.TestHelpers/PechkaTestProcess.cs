using Pechka.AspNet.BackgroundServices;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// Once-per-process harness state. The shared Postgres container and the lane pool are
/// process-wide, so a test process serves exactly one <see cref="PechkaTestApp"/>.
/// </summary>
internal static class PechkaTestProcess
{
    private static readonly object Lock = new();
    private static Type? _owner;

    public static void EnsureInitialized(PechkaTestApp app)
    {
        lock (Lock)
        {
            if (_owner == null)
            {
                // Nothing ticks on a timer anywhere in the process: background work is driven
                // explicitly (SyncTickingServicesAsync / DrainBackgroundJobsAsync), so a test
                // observes exactly the work it asked for instead of racing a timer. This is the
                // second lever next to DisablePechkaBackgroundAutoStart on each host.
                TickingServiceWorkerBase.IntervalOverride = Timeout.InfiniteTimeSpan;
                _owner = app.GetType();
            }
            else if (_owner != app.GetType())
                throw new InvalidOperationException(
                    $"This test process already hosts {_owner.Name}; the Pechka test harness " +
                    $"supports one PechkaTestApp per process and {app.GetType().Name} is a second one.");
        }
    }
}
