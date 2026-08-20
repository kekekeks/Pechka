using Microsoft.Extensions.DependencyInjection;
using MyWebApp;
using Pechka.AspNet.Jobs;
using Pechka.AspNet.TestHelpers;

namespace MyWebApp.Tests;

public class IsolatedHostTests : IClassFixture<IsolatedHosts>
{
    private readonly IsolatedHosts _hosts;

    public IsolatedHostTests(IsolatedHosts hosts) => _hosts = hosts;

    private static async Task<string[]> Names(PechkaTestHost host)
    {
        using var rpc = host.CreateRpcSession();
        return (await rpc.Call((TodoRpc r) => r.List())).Select(x => x.Name).ToArray();
    }

    [Fact]
    public async Task A_Fresh_Host_Starts_On_A_Virgin_Fully_Migrated_Schema()
    {
        var host = await _hosts.StartAsync();
        // Migrations ran on start (List would fail without the schema) and nothing else lives here
        Assert.Empty(await Names(host));
    }

    [Fact]
    public async Task A_Second_Host_On_The_Same_Database_Sees_Its_Data()
    {
        var first = await _hosts.StartAsync();
        var name = TestData.Unique("shared");
        using (var rpc = first.CreateRpcSession())
            await rpc.Call((TodoRpc r) => r.AddPair(name, TestData.Unique("second")));

        var second = await _hosts.StartOnExistingDatabaseAsync(first.ConnectionString);

        Assert.Contains(name, await Names(second));
    }

    [Fact]
    public async Task An_Unreachable_Database_Is_A_Named_Startup_Failure_Not_A_Hang()
    {
        var failure = await _hosts.TryStartAsync(
            "--Database:ConnectionString",
            "Host=127.0.0.1;Port=1;Username=nobody;Password=nothing;Database=nowhere;Timeout=3");
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task A_Probe_Job_Type_Registers_Through_The_Harness_Services_Seam()
    {
        var host = await _hosts.StartAsync(
            services => services.AddBackgroundJob<ProbeJob, ProbeJobHandler>());
        var name = TestData.Unique("probe");
        await host.WithScopeAsync(services => services.GetRequiredService<IBackgroundJobScheduler>()
            .Enqueue(new ProbeJob { Name = name }));

        var processed = await host.RunPendingJobsAsync();

        Assert.True(processed >= 1);
        Assert.Contains($"probe-{name}", await Names(host));
    }
}
