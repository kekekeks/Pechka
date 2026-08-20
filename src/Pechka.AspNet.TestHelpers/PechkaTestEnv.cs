namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// The per-test-class lease on a pooled lane host — the class-fixture base. Deliberately free of
/// any xunit dependency: with xunit v2, deriving <c>class TestEnv : PechkaTestEnv&lt;MyApp&gt;,
/// IAsyncLifetime</c> binds <see cref="InitializeAsync"/>/<see cref="DisposeAsync"/> implicitly;
/// xunit v3 (ValueTask-based lifetime) needs a two-line shim forwarding to them.
///
/// <para>The lane (host, database, long-lived scope) outlives the lease; see
/// <see cref="PechkaTestHost.Resolve{T}"/> for the sharing contract.</para>
/// </summary>
public abstract class PechkaTestEnv<TApp> : IAsyncDisposable where TApp : PechkaTestApp, new()
{
    private PechkaTestLane? _lane;

    private PechkaTestLane Lane => _lane ?? throw new InvalidOperationException(
        "The test environment is not initialized — the test framework must run InitializeAsync " +
        "before the class's first test (with xunit v2, implement IAsyncLifetime on the fixture).");

    public virtual async Task InitializeAsync() => _lane = await TestLanePool<TApp>.AcquireAsync();

    public virtual Task DisposeAsync()
    {
        if (_lane is { } lane)
        {
            _lane = null;
            TestLanePool<TApp>.Release(lane);
        }
        return Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    public PechkaTestHost Host => Lane.Host;
    public TApp App => (TApp)Host.App;

    /// <summary>Which lane this class leased, 0-based. Diagnostic only — never branch on it.</summary>
    public int LaneIndex => Lane.Index;

    public string BaseUrl => Host.BaseUrl;
    public int Port => Host.Port;
    public string ConnectionString => Host.ConnectionString;

    /// <inheritdoc cref="PechkaTestHost.Resolve{T}"/>
    public T Resolve<T>() where T : notnull => Host.Resolve<T>();

    /// <inheritdoc cref="PechkaTestHost.WithScopeAsync{T}"/>
    public Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action) => Host.WithScopeAsync(action);

    public Task WithScopeAsync(Func<IServiceProvider, Task> action) => Host.WithScopeAsync(action);

    /// <inheritdoc cref="PechkaTestHost.CreateRpcSession"/>
    public RpcSession CreateRpcSession() => Host.CreateRpcSession();

    /// <inheritdoc cref="PechkaTestHost.CreateClient"/>
    public HttpClient CreateClient() => Host.CreateClient();

    /// <inheritdoc cref="PechkaTestHost.RunPendingJobsAsync"/>
    public Task<int> DrainBackgroundJobsAsync() => Host.RunPendingJobsAsync();

    /// <inheritdoc cref="PechkaTestHost.SyncTickingServicesAsync"/>
    public Task SyncTickingServicesAsync() => Host.SyncTickingServicesAsync();
}
