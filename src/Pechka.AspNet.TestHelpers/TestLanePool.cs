using System.Collections.Concurrent;

namespace Pechka.AspNet.TestHelpers;

/// <summary>A pooled host plus its lane number. The index names the database and appears in
/// start-failure messages so an unbootable host is attributable to one lane.</summary>
internal sealed class PechkaTestLane
{
    public PechkaTestLane(int index, PechkaTestHost host)
    {
        Index = index;
        Host = host;
    }

    public int Index { get; }
    public PechkaTestHost Host { get; }
}

/// <summary>
/// The process-wide pool of lane hosts for one app — the suite's parallelism unit. A lane is one
/// host on its own <c>lane_&lt;n&gt;</c> database, leased exclusively to one test class at a time
/// via <see cref="PechkaTestEnv{TApp}"/> and handed to the next class when that one finishes.
///
/// <para><b>A lane is concurrency isolation, not a clean slate.</b> Its database is reused by
/// every later class that lands on it; tests isolate by uniqueness (Guid-suffixed names and
/// identifiers), never by truncation or global-count assertions.</para>
///
/// <para>Lanes are created on demand, never up front — a filtered single-class run starts one
/// host. xunit is the scheduler: keep its <c>maxParallelThreads</c> equal to the pool size.
/// Lane hosts are never torn down; the process exit ends them and Ryuk reaps the container.</para>
/// </summary>
internal static class TestLanePool<TApp> where TApp : PechkaTestApp, new()
{
    private static readonly TApp App = new();
    private static readonly int Size = ResolveSize();
    private static readonly SemaphoreSlim Permits = new(Size, Size);
    private static readonly ConcurrentBag<PechkaTestLane> Free = new();
    private static int _nextIndex = -1;

    private static int ResolveSize()
    {
        var configured = Environment.GetEnvironmentVariable("PECHKA_TEST_LANES");
        if (int.TryParse(configured, out var lanes) && lanes > 0)
            return lanes;
        return App.DefaultLaneCount;
    }

    /// <summary>Takes an exclusive lane, waiting asynchronously until one is free (a blocking
    /// wait would pin the test-framework thread the lane's current owner needs to finish).</summary>
    public static async Task<PechkaTestLane> AcquireAsync()
    {
        await Permits.WaitAsync();
        try
        {
            if (Free.TryTake(out var free))
                return free;

            var index = Interlocked.Increment(ref _nextIndex);
            var connectionString = await PechkaSharedPostgres.CreateDatabaseAsync(App, $"lane_{index}");
            var host = await PechkaTestHost.StartAsync(App, connectionString, null,
                Array.Empty<string>(), $"Test lane {index}");
            return new PechkaTestLane(index, host);
        }
        catch
        {
            // Nothing owns the permit until a lane is returned; a throw here would otherwise
            // shrink the pool by one for the rest of the run and eventually deadlock it.
            Permits.Release();
            throw;
        }
    }

    /// <summary>Returns a lane, host and database exactly as the class left them.</summary>
    public static void Release(PechkaTestLane lane)
    {
        Free.Add(lane);
        Permits.Release();
    }
}
