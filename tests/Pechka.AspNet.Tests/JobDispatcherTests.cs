using Microsoft.Extensions.DependencyInjection;
using Pechka.AspNet.Jobs;

namespace Pechka.AspNet.Tests;

public class JobDispatcherTests : SqliteTestBase
{
    public JobDispatcherTests(SqliteTestDatabase db) : base(db)
    {
    }

    private static string JobType => typeof(TestJob).FullName!;

    private static string Payload(string name) => $$"""{"Name":"{{name}}"}""";

    [Fact]
    public async Task RunPendingJobs_Resolves_From_DI_As_The_Poller()
    {
        await using var host = JobTestHost.Create(Db);
        var dispatcher = host.Services.GetRequiredService<IBackgroundJobDispatcher>();
        Assert.Same(host.Poller, dispatcher);
    }

    [Fact]
    public async Task RunPendingJobs_Returns_The_Number_Of_Processed_Jobs_Including_Failed_Ones()
    {
        await using var host = JobTestHost.Create(Db);
        host.Log.Behavior = (name, _) => name == "boom"
            ? throw new InvalidOperationException("intentional")
            : Task.CompletedTask;
        foreach (var name in new[] { "a", "boom", "b" })
            await host.InsertRawJob(JobType, Payload(name));

        var dispatcher = host.Services.GetRequiredService<IBackgroundJobDispatcher>();
        var processed = await dispatcher.RunPendingJobsAsync();

        Assert.Equal(3, processed);
        var jobs = await host.Jobs();
        Assert.Equal(new[] { JobState.Completed, JobState.Failed, JobState.Completed },
            jobs.Select(j => j.State));
    }

    [Fact]
    public async Task RunPendingJobs_Returns_Zero_For_An_Empty_Queue()
    {
        await using var host = JobTestHost.Create(Db);
        var dispatcher = host.Services.GetRequiredService<IBackgroundJobDispatcher>();
        Assert.Equal(0, await dispatcher.RunPendingJobsAsync());
    }

    [Fact]
    public async Task RunPendingJobs_Counts_A_Job_Restarted_Via_The_Database()
    {
        await using var host = JobTestHost.Create(Db);
        host.Log.Behavior = (_, invocation) => invocation == 1
            ? throw new InvalidOperationException("first run fails")
            : Task.CompletedTask;
        var id = await host.InsertRawJob(JobType, Payload("a"));
        var dispatcher = host.Services.GetRequiredService<IBackgroundJobDispatcher>();

        Assert.Equal(1, await dispatcher.RunPendingJobsAsync());
        Assert.Equal(JobState.Failed, (await host.Job(id)).State);

        await host.SetState(id, JobState.Pending);
        Assert.Equal(1, await dispatcher.RunPendingJobsAsync());
        var job = await host.Job(id);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, job.Attempts);
    }
}
