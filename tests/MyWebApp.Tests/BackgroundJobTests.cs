using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Mapping;
using Microsoft.Extensions.DependencyInjection;
using MyWebApp;
using Pechka.AspNet.TestHelpers;

namespace MyWebApp.Tests;

public class BackgroundJobTests : IClassFixture<TestEnv>
{
    [Table("BackgroundJobs")]
    private class JobRow
    {
        [PrimaryKey] public long Id { get; set; }
        [Column] public int State { get; set; }
        [Column] public string? Error { get; set; }
    }

    private readonly TestEnv _env;

    public BackgroundJobTests(TestEnv env) => _env = env;

    private async Task<string[]> Names()
    {
        using var rpc = _env.CreateRpcSession();
        return (await rpc.Call((TodoRpc r) => r.List())).Select(x => x.Name).ToArray();
    }

    [Fact]
    public async Task An_Enqueued_Job_Runs_On_An_Explicit_Drain_And_Never_On_Its_Own()
    {
        using var rpc = _env.CreateRpcSession();
        var name = TestData.Unique("greeting");
        await rpc.Call((TodoRpc r) => r.EnqueueGreeting(name));

        // Nothing ticks on a timer in the harness; the effect appears only after the drain
        Assert.DoesNotContain($"greeting-{name}", await Names());
        var processed = await _env.DrainBackgroundJobsAsync();

        Assert.True(processed >= 1);
        Assert.Contains($"greeting-{name}", await Names());
    }

    [Fact]
    public async Task A_Rolled_Back_Enqueue_Never_Produces_A_Job()
    {
        using var rpc = _env.CreateRpcSession();
        var name = TestData.Unique("phantom");
        await Assert.ThrowsAsync<RpcServerException>(
            () => rpc.Call((TodoRpc r) => r.EnqueueGreetingFailing(name)));

        await _env.DrainBackgroundJobsAsync();

        Assert.DoesNotContain($"greeting-{name}", await Names());
    }

    [Fact]
    public async Task A_Failing_Job_Stores_Its_Exception_And_Does_Not_Block_The_Queue()
    {
        using var rpc = _env.CreateRpcSession();
        var reason = TestData.Unique("reason");
        var flakyId = await rpc.Call((TodoRpc r) => r.EnqueueFlaky(reason));
        var name = TestData.Unique("after-failure");
        await rpc.Call((TodoRpc r) => r.EnqueueGreeting(name));

        await _env.DrainBackgroundJobsAsync();

        Assert.Contains($"greeting-{name}", await Names());
        var row = await _env.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<MyDbContextManager>();
            return await db.ExecAsync(ctx => ctx.GetTable<JobRow>().FirstAsync(x => x.Id == flakyId));
        });
        Assert.Equal(3, row.State); // Failed
        Assert.Contains(reason, row.Error);
    }
}
