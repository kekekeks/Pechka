using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pechka.AspNet.BackgroundServices;
using Pechka.AspNet.Jobs;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// One running app host on its own Kestrel port and database, plus the long-lived DI scope
/// <see cref="Resolve{T}"/> serves from. The same surface backs pooled lanes and isolated hosts,
/// so a scenario reads identically against either.
/// </summary>
public sealed class PechkaTestHost : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;

    private PechkaTestHost(PechkaTestApp app, IHost host, int port, string connectionString)
    {
        App = app;
        Host = host;
        Port = port;
        ConnectionString = connectionString;
        _scope = host.Services.CreateAsyncScope();
    }

    public PechkaTestApp App { get; }
    public IHost Host { get; }
    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>This host's database, so a second host can be started on the same one through
    /// <see cref="PechkaIsolatedHosts{TApp}.StartOnExistingDatabaseAsync(string, string[])"/>.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// In-process service access from one long-lived DI scope this object owns. Every caller
    /// shares that scope — and with it one transactional context manager and its single
    /// transaction slot — so use it for read probes and stateless services only, and never open a
    /// transaction scope on anything it hands out: an open transaction would be inherited by the
    /// next test (or, on a lane, the next class). Work that owns a transaction goes through
    /// <see cref="WithScopeAsync{T}"/>.
    /// </summary>
    public T Resolve<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>A fresh DI scope for one unit of work.</summary>
    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task WithScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    /// <summary>A fresh cookie-jar RPC client — one logical browser session.</summary>
    public RpcSession CreateRpcSession() => App.CreateRpcSession(BaseUrl);

    /// <summary>A plain HTTP client with its own cookie jar, based at this host.</summary>
    public HttpClient CreateClient() =>
        new(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri(BaseUrl) };

    /// <summary>Drains this host's background job queue (a full FIFO drain — a job enqueued by a
    /// handler is picked up in the same drain) and returns how many jobs were processed.</summary>
    public Task<int> RunPendingJobsAsync()
    {
        var dispatcher = Host.Services.GetService<IBackgroundJobDispatcher>()
                         ?? throw new InvalidOperationException(
                             "The app does not use background jobs (AddBackgroundJobs was not called).");
        return dispatcher.RunPendingJobsAsync();
    }

    /// <summary>Runs one tick of every registered ticking service and waits for completion.</summary>
    public Task SyncTickingServicesAsync() =>
        Host.Services.GetRequiredService<ITickingServiceManager>().SyncAllServices(CancellationToken.None);

    internal static async Task<PechkaTestHost> StartAsync(PechkaTestApp app, string connectionString,
        Action<IServiceCollection>? configureServices, string[] extraArgs, string what)
    {
        var (host, failure) = await TryStartAsync(app, connectionString, configureServices, extraArgs);
        if (failure != null)
            // Named, because a host that cannot boot fails every test handed to it and the stack
            // alone would not say which host, port or database.
            throw new InvalidOperationException($"{what} could not start its host.", failure);
        return host!;
    }

    internal static async Task<(PechkaTestHost? Host, Exception? Failure)> TryStartAsync(PechkaTestApp app,
        string connectionString, Action<IServiceCollection>? configureServices, string[] extraArgs)
    {
        PechkaTestProcess.EnsureInitialized(app);

        var port = TestPorts.GetFreePort();
        var args = new List<string>
        {
            "--urls", $"http://127.0.0.1:{port}",
            "--contentRoot", app.AppDirectory,
            $"--{app.DatabaseConfigSection}:ConnectionString", connectionString,
        };
        args.AddRange(app.DefaultArgs);
        args.AddRange(extraArgs);

        IHost? host = null;
        PechkaTestHost? testHost = null;
        try
        {
            var program = app.CreateProgram(args.ToArray());
            // Appended after the app's own composition (CustomizeHost is additive), so the app
            // cannot lose the harness levers and the harness cannot clobber the app's.
            program.CustomizeHost((hostBuilder, _) => hostBuilder.ConfigureServices(services =>
            {
                services.DisablePechkaBackgroundAutoStart();
                ForceDisableTsApiGeneration(services);
                app.ConfigureServices(services);
                configureServices?.Invoke(services);
            }));
            host = program.CreateHost();
            await host.StartAsync();
            testHost = new PechkaTestHost(app, host, port, connectionString);
            await app.WaitUntilReadyAsync(testHost);
            return (testHost, null);
        }
        catch (Exception failure)
        {
            if (testHost != null)
                await testHost.DisposeSwallowingAsync();
            else if (host != null)
            {
                try
                {
                    await host.StopAsync();
                }
                catch
                {
                    // A host that failed to start may also fail to stop; the start failure is
                    // the interesting one.
                }
                host.Dispose();
            }
            return (null, failure);
        }
    }

    /// <summary>Many test hosts run from source against one app directory; the generated api.ts
    /// write is turned off so host starts need no lock. The supported producer of api.ts is
    /// <see cref="PechkaTestApp.EnsureTsApiGeneratedAsync"/>.</summary>
    private static void ForceDisableTsApiGeneration(IServiceCollection services)
    {
        foreach (var descriptor in services)
            if (descriptor.ServiceType == typeof(PechkaConfiguration)
                && descriptor.ImplementationInstance is PechkaConfiguration config)
                config.DisableTsApiGeneration = true;
    }

    internal async Task DisposeSwallowingAsync()
    {
        try
        {
            await DisposeAsync();
        }
        catch
        {
            // Teardown only; the scenario (or start failure) has already reported its result.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await Host.StopAsync();
        Host.Dispose();
    }
}
