using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// Class fixture starting extra app hosts for the scenarios a shared lane cannot serve:
/// startup-configuration cases, and cases that corrupt or observe state the rest of the suite
/// relies on. Isolation is a freshly created database on the process-shared container — a virgin,
/// fully migrated schema per host without container startup cost. As with
/// <see cref="PechkaTestEnv{TApp}"/>, xunit v2 binds the lifetime methods implicitly via
/// <c>IAsyncLifetime</c> on a derived class.
/// </summary>
public class PechkaIsolatedHosts<TApp> : IAsyncDisposable where TApp : PechkaTestApp, new()
{
    private readonly TApp _app = new();
    private readonly List<PechkaTestHost> _started = new();

    public virtual Task InitializeAsync() => Task.CompletedTask;

    /// <summary>A normally configured host on its own fresh database. Extra arguments go through
    /// the real command-line configuration surface, exactly as a deployment would supply them.</summary>
    public Task<PechkaTestHost> StartAsync(params string[] extraArgs) =>
        StartCoreThrowingAsync(extraArgs, null, null);

    /// <summary>A host with extra harness service registrations — for probe job types and
    /// in-process transport doubles that a command-line argument cannot express. The standard
    /// harness levers are applied first, so a caller cannot lose them.</summary>
    public Task<PechkaTestHost> StartAsync(Action<IServiceCollection> configureServices,
        params string[] extraArgs) =>
        StartCoreThrowingAsync(extraArgs, configureServices, null);

    /// <summary>A host on a database that already exists — the one arrangement a fresh database
    /// cannot make: a schema whose migrations were applied by an earlier host.</summary>
    public Task<PechkaTestHost> StartOnExistingDatabaseAsync(string connectionString,
        params string[] extraArgs) =>
        StartCoreThrowingAsync(extraArgs, null, connectionString);

    public Task<PechkaTestHost> StartOnExistingDatabaseAsync(string connectionString,
        Action<IServiceCollection> configureServices, params string[] extraArgs) =>
        StartCoreThrowingAsync(extraArgs, configureServices, connectionString);

    /// <summary>Like <see cref="StartAsync(string[])"/> but returns the startup failure instead
    /// of throwing — for scenarios asserting that a configuration value stops the host from
    /// serving. Returns null when the host did start (it is then owned and disposed by this
    /// fixture like any other).</summary>
    public async Task<Exception?> TryStartAsync(params string[] extraArgs) =>
        (await StartCoreAsync(extraArgs, null, null)).Failure;

    public async Task<Exception?> TryStartAsync(Action<IServiceCollection> configureServices,
        params string[] extraArgs) =>
        (await StartCoreAsync(extraArgs, configureServices, null)).Failure;

    private async Task<PechkaTestHost> StartCoreThrowingAsync(string[] extraArgs,
        Action<IServiceCollection>? configureServices, string? existingDatabase)
    {
        var (host, failure) = await StartCoreAsync(extraArgs, configureServices, existingDatabase);
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return host!;
    }

    private async Task<(PechkaTestHost? Host, Exception? Failure)> StartCoreAsync(string[] extraArgs,
        Action<IServiceCollection>? configureServices, string? existingDatabase)
    {
        var connectionString = existingDatabase
                               ?? await PechkaSharedPostgres.CreateDatabaseAsync(_app,
                                   $"isolated_{Guid.NewGuid():N}");
        var (host, failure) = await PechkaTestHost.TryStartAsync(_app, connectionString,
            configureServices, extraArgs);
        if (host != null)
            lock (_started)
                _started.Add(host);
        return (host, failure);
    }

    public virtual async Task DisposeAsync()
    {
        // A host whose database was deliberately corrupted can fault on the way down; every host
        // gets its own attempt so one failure doesn't strand the ports and pools of the rest.
        // The Postgres container is process-shared and deliberately not touched here.
        List<PechkaTestHost> started;
        lock (_started)
        {
            started = new List<PechkaTestHost>(_started);
            _started.Clear();
        }
        foreach (var host in started)
            await host.DisposeSwallowingAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();
}
