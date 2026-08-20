using LinqToDB;
using MyWebApp;
using Pechka.AspNet;
using Pechka.AspNet.TestHelpers;

namespace MyWebApp.Tests;

/// <summary>The one app descriptor for this test process (the consumer-side seam of
/// Pechka.AspNet.TestHelpers).</summary>
public class MyWebAppTestApp : PechkaTestApp
{
    public override IPechkaProgramBuilderExecutable CreateProgram(string[] args) =>
        MyWebAppProgram.Create(args);

    public override string AppDirectory =>
        Path.Combine(RepoRoot.Find("Pechka.sln"), "samples", "MyWebApp");

    // Keep equal to maxParallelThreads in xunit.runner.json
    public override int DefaultLaneCount => 3;

    public override Task EnsureFrontendBuiltAsync() =>
        FrontendBuild.EnsureBuiltAsync(Path.Combine(AppDirectory, "webapp"), buildCommand: "npm run dist");
}

/// <summary>Per-class lane lease; the xunit v2 lifetime binds to the base's methods implicitly.</summary>
public class TestEnv : PechkaTestEnv<MyWebAppTestApp>, IAsyncLifetime;

public class IsolatedHosts : PechkaIsolatedHosts<MyWebAppTestApp>, IAsyncLifetime;

public class PlaywrightFixture : PechkaPlaywrightFixture<MyWebAppTestApp>, IAsyncLifetime;

[CollectionDefinition("E2E", DisableParallelization = true)]
public class E2ECollection : ICollectionFixture<PlaywrightFixture>;

public static class TestData
{
    /// <summary>Lanes are reused across classes, so isolation is by uniqueness, never by
    /// truncation or global counts.</summary>
    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

/// <summary>A job type that must never exist in production — registered per host through the
/// harness services seam.</summary>
public class ProbeJob
{
    public string Name { get; set; } = "";
}

public class ProbeJobHandler : Pechka.AspNet.Jobs.IBackgroundJobHandler<ProbeJob>
{
    private readonly MyDbContextManager _db;

    public ProbeJobHandler(MyDbContextManager db) => _db = db;

    public Task Execute(ProbeJob job, CancellationToken token)
        => _db.ExecAsync(db => db.InsertAsync(new TodoItem { Name = $"probe-{job.Name}" }, token: token));
}
