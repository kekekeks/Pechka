using Microsoft.Extensions.DependencyInjection;
using Pechka.AspNet.Database;
using Pechka.AspNet.Jobs;

namespace Pechka.AspNet.Tests;

public class JobSchedulerTests : SqliteTestBase
{
    public JobSchedulerTests(SqliteTestDatabase db) : base(db)
    {
    }

    [Fact]
    public async Task Enqueue_Without_An_Ambient_Scope_Is_Visible_Immediately()
    {
        await using var host = JobTestHost.Create(Db);
        await using var scope = host.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
            .Enqueue(new TestJob { Name = "a" });

        var job = await host.Job(id);
        Assert.Equal(typeof(TestJob).FullName, job.Type);
        Assert.Equal(JobState.Pending, job.State);
        Assert.Contains("\"Name\":\"a\"", job.Payload);
    }

    [Fact]
    public async Task Enqueue_In_A_Rolled_Back_Scope_Leaves_No_Row()
    {
        await using var host = JobTestHost.Create(Db);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();
            var tx = manager.BeginTransaction();
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
                .Enqueue(new TestJob { Name = "a" });
            await tx.RollbackAsync();
        }
        Assert.Empty(await host.Jobs());
    }

    [Fact]
    public async Task Enqueue_In_An_Uncommitted_Scope_Is_Invisible_To_A_Drain()
    {
        await using var host = JobTestHost.Create(Db);
        await using var scope = host.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();
        var tx = manager.BeginTransaction();
        await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
            .Enqueue(new TestJob { Name = "a" });

        await host.Drain();
        Assert.Empty(host.Log.Executed);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Enqueue_In_A_Committed_Scope_Is_Executed_By_The_Poller()
    {
        await using var host = JobTestHost.Create(Db);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();
            var tx = manager.BeginTransaction();
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
                .Enqueue(new TestJob { Name = "a" });
            await tx.CommitAsync();
        }

        await host.Drain();
        Assert.Equal(new[] { "a" }, host.Log.Executed);
    }

    [Fact]
    public async Task Enqueue_Of_An_Unregistered_Job_Type_Throws()
    {
        await using var host = JobTestHost.Create(Db);
        await using var scope = host.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
            .GetRequiredService<IBackgroundJobScheduler>().Enqueue(new UnregisteredJob()));
    }

    [Fact]
    public async Task The_Wake_Up_Is_Deferred_Until_The_Enqueuing_Transaction_Commits()
    {
        // Long polling interval: only the enqueue wake-up can make the running poller pick the job up
        await using var host = JobTestHost.Create(Db, configureJobs: o => o.PollingInterval = TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();
        host.Poller.Start(cts.Token, host.LoggerFactory);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var manager = scope.ServiceProvider.GetRequiredService<TestDbManager>();
            var tx = manager.BeginTransaction();
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
                .Enqueue(new TestJob { Name = "a" });

            await Task.Delay(200);
            Assert.Empty(host.Log.Executed);

            await tx.CommitAsync();
            await Poll.Until(() => host.Log.Executed.Count == 1, "the poller to pick up the committed job");
        }
        finally
        {
            cts.Cancel();
            await host.Poller.Completion;
        }
    }

    private class UnregisteredJob
    {
    }
}
