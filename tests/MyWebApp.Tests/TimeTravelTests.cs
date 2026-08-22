using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using MyWebApp;
using Pechka.AspNet.Jobs;

namespace MyWebApp.Tests;

/// <summary>Drives the default-on lane clock: forward travel on a leased lane is safe because it
/// is indistinguishable from running the suite later in real time.</summary>
public class TimeTravelTests : IClassFixture<TestEnv>
{
    private readonly TestEnv _env;

    public TimeTravelTests(TestEnv env) => _env = env;

    private Task<long> Enqueue(string name, TimeSpan? expiresIn = null) =>
        _env.WithScopeAsync(services => services.GetRequiredService<IBackgroundJobScheduler>()
            .Enqueue(new GreetingJob { Name = name }, expiresIn));

    private Task<JobRow?> Job(long id) =>
        _env.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<MyDbContextManager>();
            return await db.ExecAsync(ctx =>
                ctx.GetTable<JobRow>().FirstOrDefaultAsync(x => x.Id == id));
        });

    [Fact]
    public async Task A_Job_Not_Started_In_Time_Auto_Fails_After_Forward_Travel()
    {
        var name = TestData.Unique("late");
        var id = await Enqueue(name, expiresIn: TimeSpan.FromHours(1));

        _env.Clock.Advance(TimeSpan.FromHours(2));
        await _env.DrainBackgroundJobsAsync();

        using var rpc = _env.CreateRpcSession();
        Assert.DoesNotContain($"greeting-{name}",
            (await rpc.Call((TodoRpc r) => r.List())).Select(x => x.Name));
        var job = await Job(id);
        Assert.Equal(3, job!.State); // Failed
        Assert.Equal("Job expired before execution", job.Error);
    }

    [Fact]
    public async Task Completed_Rows_Are_Purged_Once_Retention_Elapses_Via_Travel()
    {
        var id = await Enqueue(TestData.Unique("kept"));
        await _env.DrainBackgroundJobsAsync();
        Assert.NotNull(await Job(id));

        // The sample configures CompletedJobRetention = 1h; travel past it and let the next
        // drain's maintenance sweep collect the row
        _env.Clock.Advance(TimeSpan.FromHours(2));
        await _env.DrainBackgroundJobsAsync();

        Assert.Null(await Job(id));
    }
}
