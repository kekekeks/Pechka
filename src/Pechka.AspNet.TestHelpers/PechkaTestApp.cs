using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// Describes one Pechka-based application to the test harness. Implemented once per consuming
/// app's test suite; everything app-dependent — how the host is composed, where the app project
/// lives, config section names, readiness, the SPA build — hangs off this class as an overridable
/// hook. The harness supports one app per test process (the Postgres container and lane pool are
/// process-wide).
/// </summary>
public abstract class PechkaTestApp
{
    private static readonly ConcurrentDictionary<Type, Lazy<Task>> TsApiGenerated = new();

    /// <summary>The app's full production composition — its Program refactored to expose the
    /// builder, e.g. <c>MyAppProgram.Create(args)</c>. The harness appends its own host
    /// customization on top, so the app's own CustomizeHost calls are preserved.</summary>
    public abstract IPechkaProgramBuilderExecutable CreateProgram(string[] args);

    /// <summary>Content root of the app project — the directory holding config.defaults.json and
    /// the web app paths. Typically <c>Path.Combine(RepoRoot.Find("MyApp.sln"), "src", "MyApp")</c>.
    /// Passed to every host as <c>--contentRoot</c>.</summary>
    public abstract string AppDirectory { get; }

    /// <summary>Config section holding the connection string the harness injects
    /// (<c>--{section}:ConnectionString</c>).</summary>
    public virtual string DatabaseConfigSection => "Database";

    /// <summary>Stock config overrides every test host gets, through the real command-line
    /// configuration surface (mock providers, shortened cooldowns, ...).</summary>
    public virtual string[] DefaultArgs => Array.Empty<string>();

    /// <summary>Where Pechka mounts the CoreRPC endpoint.</summary>
    public virtual string RpcPath => "/tsrpc";

    public virtual string PostgresImage => "postgres:16";

    /// <summary>Applied to every connection string the harness hands out. The default bounds
    /// Npgsql's per-host pool so no single host can exhaust the shared server's backends.</summary>
    public virtual string AmendConnectionString(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        builder["Maximum Pool Size"] = 20;
        return builder.ConnectionString;
    }

    /// <summary>Harness service registrations applied to every host (probe job types, transport
    /// doubles whose seam is an in-process handle). Anything expressible as a command-line
    /// argument should arrive via <see cref="DefaultArgs"/> or per-start extra args instead.</summary>
    public virtual void ConfigureServices(IServiceCollection services)
    {
    }

    /// <summary>How many pooled lane hosts may exist at once; <c>PECHKA_TEST_LANES</c> overrides.
    /// Keep equal to xunit's <c>maxParallelThreads</c> or one of the two silently stops being the
    /// binding constraint.</summary>
    public virtual int DefaultLaneCount => Math.Min(Environment.ProcessorCount, 12);

    public virtual RpcSession CreateRpcSession(string baseUrl) => new(baseUrl, RpcPath);

    /// <summary>Called after the host started. The default awaits the startup migrations and then
    /// polls until the Kestrel endpoint answers; apps with a real health endpoint that probes
    /// deeper should override.</summary>
    public virtual async Task WaitUntilReadyAsync(PechkaTestHost host, CancellationToken token = default)
    {
        var ready = host.Host.Services.GetService<DatabaseReadySignal>();
        if (ready != null)
            await ready.Ready.WaitAsync(TimeSpan.FromSeconds(30), token);
        using var http = new HttpClient();
        await Poll.UntilAsync(async () =>
        {
            await http.GetAsync(host.BaseUrl, token);
            return true;
        }, what: $"host on {host.BaseUrl} answering");
    }

    /// <summary>SPA build hook, called by the Playwright fixture before the first browser test
    /// (after <see cref="EnsureTsApiGeneratedAsync"/>). Typically forwards to
    /// <see cref="FrontendBuild.EnsureBuiltAsync"/>; the default is a no-op for apps without a
    /// frontend.</summary>
    public virtual Task EnsureFrontendBuiltAsync() => Task.CompletedTask;

    /// <summary>
    /// Writes the generated TypeScript API once per process. Regular test hosts start with
    /// generation forced off (many hosts writing one file would race), so the Playwright fixture
    /// calls this before the frontend build to keep a fresh clone's typecheck working.
    /// </summary>
    public Task EnsureTsApiGeneratedAsync() =>
        TsApiGenerated.GetOrAdd(GetType(), _ => new Lazy<Task>(GenerateTsApiAsync)).Value;

    /// <summary>Builds (but never starts) a throwaway host to render the TS API from the app's
    /// real RPC surface, then writes it to the app's configured api path.</summary>
    protected virtual async Task GenerateTsApiAsync()
    {
        using var host = CreateProgram(new[] { "--contentRoot", AppDirectory }).CreateHost();
        var config = host.Services.GetRequiredService<PechkaConfiguration>();
        var interop = host.Services.GetRequiredService<TsInterop>();
        var path = Path.Combine(AppDirectory, config.WebAppApiPath);
        await File.WriteAllTextAsync(path, interop.GenerateTsRpc());
    }
}
