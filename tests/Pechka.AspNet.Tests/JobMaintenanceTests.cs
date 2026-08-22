using Microsoft.Extensions.DependencyInjection;
using Pechka.AspNet.Jobs;

namespace Pechka.AspNet.Tests;

public class JobMaintenanceTests : SqliteTestBase
{
    public JobMaintenanceTests(SqliteTestDatabase db) : base(db)
    {
    }

    private static string JobType => typeof(TestJob).FullName!;

    private static string Payload(string name) => $$"""{"Name":"{{name}}"}""";

    [Fact]
    public async Task An_Expired_Pending_Job_Is_Never_Executed_And_Gets_Auto_Failed()
    {
        await using var host = JobTestHost.Create(Db);
        var id = await host.InsertRawJob(JobType, Payload("late"),
            expiresAt: JobTime.UtcNow - TimeSpan.FromSeconds(1));

        var dispatcher = (IBackgroundJobDispatcher)host.Poller;
        Assert.Equal(0, await dispatcher.RunPendingJobsAsync());

        Assert.Empty(host.Log.Executed);
        var job = await host.Job(id);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("Job expired before execution", job.Error);
        Assert.NotNull(job.FinishedAt);
    }

    [Fact]
    public async Task A_Not_Yet_Expired_Job_Executes_Normally()
    {
        await using var host = JobTestHost.Create(Db);
        var id = await host.InsertRawJob(JobType, Payload("in-time"),
            expiresAt: JobTime.UtcNow + TimeSpan.FromMinutes(5));

        await host.Drain();

        Assert.Equal(new[] { "in-time" }, host.Log.Executed);
        Assert.Equal(JobState.Completed, (await host.Job(id)).State);
    }

    [Fact]
    public async Task Enqueue_Applies_The_Job_Types_Default_Expiration()
    {
        await using var host = JobTestHost.Create(Db);
        await using var scope = host.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
            .Enqueue(new ExpiringTestJob { Name = "a" });

        var job = await host.Job(id);
        Assert.NotNull(job.ExpiresAt);
        var expiresIn = job.ExpiresAt!.Value - JobTime.UtcNow;
        Assert.InRange(expiresIn, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task Per_Enqueue_Expiration_Overrides_The_Type_Default()
    {
        await using var host = JobTestHost.Create(Db);
        await using var scope = host.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
            .Enqueue(new ExpiringTestJob { Name = "a" }, expiresIn: TimeSpan.FromHours(1));

        var job = await host.Job(id);
        var expiresIn = job.ExpiresAt!.Value - JobTime.UtcNow;
        Assert.InRange(expiresIn, TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(65));
    }

    [Fact]
    public async Task A_Job_Without_Configured_Expiration_Never_Expires()
    {
        await using var host = JobTestHost.Create(Db);
        await using var scope = host.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
            .Enqueue(new TestJob { Name = "a" });

        Assert.Null((await host.Job(id)).ExpiresAt);
    }

    [Fact]
    public async Task Enqueue_Stamps_Times_From_The_Registered_TimeProvider()
    {
        var clock = new TestClock { Offset = TimeSpan.FromDays(1) };
        await using var host = JobTestHost.Create(Db, timeProvider: clock);
        await using var scope = host.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
            .Enqueue(new ExpiringTestJob { Name = "a" });

        var job = await host.Job(id);
        var expectedNow = JobTime.UtcNow + TimeSpan.FromDays(1);
        Assert.InRange(job.CreatedAt, expectedNow - TimeSpan.FromMinutes(1), expectedNow + TimeSpan.FromMinutes(1));
        Assert.InRange(job.ExpiresAt!.Value,
            expectedNow + TimeSpan.FromMinutes(4), expectedNow + TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task Advancing_The_Clock_Expires_A_Pending_Job()
    {
        var clock = new TestClock();
        await using var host = JobTestHost.Create(Db, timeProvider: clock);
        await using var scope = host.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
            .Enqueue(new ExpiringTestJob { Name = "late" }); // 5-minute type default

        clock.Offset += TimeSpan.FromMinutes(10);
        await host.Drain();

        Assert.Empty(host.Log.Executed);
        var job = await host.Job(id);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("Job expired before execution", job.Error);
    }

    [Fact]
    public async Task Stale_Terminal_Rows_Are_Purged_But_Pending_And_Running_Survive()
    {
        await using var host = JobTestHost.Create(Db,
            configureJobs: o => o.StaleJobRetention = TimeSpan.FromMilliseconds(50));
        var old = JobTime.UtcNow - TimeSpan.FromMinutes(1);
        var completedId = await host.InsertRawJob(JobType, state: JobState.Completed, finishedAt: old);
        var failedId = await host.InsertRawJob(JobType, state: JobState.Failed, finishedAt: old);
        var runningId = await host.InsertRawJob(JobType, state: JobState.Running, finishedAt: old);
        var pendingId = await host.InsertRawJob("Some.Unknown.Type");

        await host.Drain();

        var remaining = (await host.Jobs()).Select(j => j.Id).ToList();
        Assert.DoesNotContain(completedId, remaining);
        Assert.DoesNotContain(failedId, remaining);
        Assert.Contains(runningId, remaining);
        Assert.Contains(pendingId, remaining);
    }

    [Fact]
    public async Task Null_StaleJobRetention_Disables_The_Purge()
    {
        await using var host = JobTestHost.Create(Db,
            configureJobs: o => o.StaleJobRetention = null);
        var id = await host.InsertRawJob(JobType, state: JobState.Failed,
            finishedAt: JobTime.UtcNow - TimeSpan.FromDays(365));

        await host.Drain();

        Assert.Equal(JobState.Failed, (await host.Job(id)).State);
    }

    [Fact]
    public async Task Fresh_Terminal_Rows_Survive_The_Default_Retention()
    {
        await using var host = JobTestHost.Create(Db);
        var id = await host.InsertRawJob(JobType, Payload("a"));

        await host.Drain();

        Assert.Equal(JobState.Completed, (await host.Job(id)).State);
    }
}
