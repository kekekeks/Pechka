using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using MyWebApp;
using Pechka.AspNet.TestHelpers;

namespace MyWebApp.Tests;

public class EnvSurfaceTests : IClassFixture<TestEnv>
{
    private readonly TestEnv _env;

    public EnvSurfaceTests(TestEnv env) => _env = env;

    [Fact]
    public async Task Resolve_Serves_Read_Probes_And_WithScope_Owns_Units_Of_Work()
    {
        var name = TestData.Unique("in-process");
        await _env.WithScopeAsync(services => services.GetRequiredService<MyDbContextManager>()
            .ExecAsync(db => db.InsertAsync(new TodoItem { Name = name })));

        // The long-lived scope is the probe surface: plain reads, no transaction scopes
        var seen = await _env.Resolve<MyDbContextManager>()
            .ExecAsync(db => db.GetTable<TodoItem>().AnyAsync(x => x.Name == name));

        Assert.True(seen);
    }

    [Fact]
    public void The_Lease_Exposes_The_Host_Surface()
    {
        Assert.StartsWith("http://127.0.0.1:", _env.BaseUrl);
        Assert.NotEmpty(_env.ConnectionString);
        Assert.True(_env.LaneIndex >= 0);
        Assert.NotNull(_env.Host.Host);
    }

    [Fact]
    public async Task Ticking_Services_Are_Parked_But_Drivable()
    {
        // No ticking services are registered in the sample; the sync must still complete
        await _env.SyncTickingServicesAsync();
    }
}
