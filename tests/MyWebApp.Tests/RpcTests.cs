using MyWebApp;
using Pechka.AspNet.TestHelpers;

namespace MyWebApp.Tests;

public class RpcTests : IClassFixture<TestEnv>
{
    private readonly TestEnv _env;

    public RpcTests(TestEnv env) => _env = env;

    private static Task<TodoItem[]> List(RpcSession rpc) => rpc.Call((TodoRpc r) => r.List());

    [Fact]
    public async Task Typed_Rpc_Round_Trips_Through_The_Real_Wire_Format()
    {
        using var rpc = _env.CreateRpcSession();
        var first = TestData.Unique("first");
        var second = TestData.Unique("second");

        var id = await rpc.Call((TodoRpc r) => r.AddPair(first, second));

        Assert.True(id > 0);
        var names = (await List(rpc)).Select(x => x.Name).ToList();
        Assert.Contains(first, names);
        Assert.Contains(second, names);
    }

    [Fact]
    public async Task A_Server_Exception_Throws_RpcServerException_And_Rolls_The_Call_Back()
    {
        using var rpc = _env.CreateRpcSession();
        var name = TestData.Unique("rolled-back");

        var e = await Assert.ThrowsAsync<RpcServerException>(
            () => rpc.Call((TodoRpc r) => r.AddPairFailing(name)));

        Assert.Contains("Intentional failure", e.Message);
        Assert.DoesNotContain(name, (await List(rpc)).Select(x => x.Name));
    }

    [Fact]
    public void CallSync_Blocks_And_Stays_Sequential()
    {
        using var rpc = _env.CreateRpcSession();
        var first = TestData.Unique("sync-first");
        var second = TestData.Unique("sync-second");

        // No awaits: each call completes before the next starts
        var id = rpc.CallSync((TodoRpc r) => r.AddPair(first, second));
        var names = rpc.CallSync((TodoRpc r) => r.List()).Select(x => x.Name).ToList();

        Assert.True(id > 0);
        Assert.Contains(first, names);
        Assert.Contains(second, names);
    }

    [Fact]
    public async Task Transient_Failures_Are_Retried_Within_The_Request_Boundary()
    {
        using var rpc = _env.CreateRpcSession();
        var name = TestData.Unique("flaky");

        var id = await rpc.Call((TodoRpc r) => r.FlakyInsert(name));

        Assert.True(id > 0);
        Assert.Contains(name, (await List(rpc)).Select(x => x.Name));
    }
}
