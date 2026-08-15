using Microsoft.Extensions.DependencyInjection;
using Pechka.AspNet.Jobs;

namespace Pechka.AspNet.Tests;

public class JobPollerTests : SqliteTestBase
{
    public JobPollerTests(SqliteTestDatabase db) : base(db)
    {
    }

    private static string JobType => typeof(TestJob).FullName!;

    private static string Payload(string name) => $$"""{"Name":"{{name}}"}""";

    [Fact]
    public async Task Drain_Executes_Pending_Jobs_In_Id_Order()
    {
        await using var host = JobTestHost.Create(Db);
        foreach (var name in new[] { "a", "b", "c" })
            await host.InsertRawJob(JobType, Payload(name));

        await host.Drain();

        Assert.Equal(new[] { "a", "b", "c" }, host.Log.Executed);
        Assert.All(await host.Jobs(), j => Assert.Equal(JobState.Completed, j.State));
    }

    [Fact]
    public async Task Successful_Job_Is_Completed_With_FinishedAt_And_Its_Writes_Committed()
    {
        await using var host = JobTestHost.Create(Db);
        var id = await host.InsertRawJob(JobType, Payload("a"));

        await host.Drain();

        var job = await host.Job(id);
        Assert.Equal(JobState.Completed, job.State);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.Error);
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task The_Claim_Marks_The_Row_Running_With_TakenAt_And_Attempts()
    {
        await using var host = JobTestHost.Create(Db);
        var id = await host.InsertRawJob(JobType, Payload("a"));

        await host.Drain();

        var claim = Assert.Single(host.Log.Claims);
        Assert.Equal(id, claim.Id);
        Assert.Equal(JobState.Running, claim.State);
        Assert.Equal(1, claim.Attempts);
        Assert.True(claim.TakenAtSet);
    }

    [Fact]
    public async Task Failing_Handler_Fails_The_Job_Rolls_Back_Its_Writes_And_The_Queue_Continues()
    {
        await using var host = JobTestHost.Create(Db);
        host.Log.Behavior = (name, _) => name == "a"
            ? throw new FakePermanentException("handler exploded")
            : Task.CompletedTask;
        var failing = await host.InsertRawJob(JobType, Payload("a"));
        var following = await host.InsertRawJob(JobType, Payload("b"));

        await host.Drain();

        var failed = await host.Job(failing);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.NotNull(failed.FinishedAt);
        Assert.Contains("handler exploded", failed.Error);
        Assert.Equal(JobState.Completed, (await host.Job(following)).State);
        // The failed handler's write was rolled back with its unit of work
        Assert.Equal(new[] { "b" }, await ReadItemNames());
    }

    [Fact]
    public async Task Unknown_Job_Type_Is_Left_Pending_For_A_Node_That_Knows_It()
    {
        await using var host = JobTestHost.Create(Db);
        var id = await host.InsertRawJob("Some.Unregistered.Job");

        await host.Drain();

        var job = await host.Job(id);
        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.Error);
    }

    [Fact]
    public async Task An_Earlier_Unknown_Job_Does_Not_Block_Later_Known_Jobs()
    {
        await using var host = JobTestHost.Create(Db);
        var unknownId = await host.InsertRawJob("Some.Unregistered.Job");
        await host.InsertRawJob(JobType, Payload("a"));

        var dispatcher = (IBackgroundJobDispatcher)host.Poller;
        Assert.Equal(1, await dispatcher.RunPendingJobsAsync());

        Assert.Equal(new[] { "a" }, host.Log.Executed);
        Assert.Equal(JobState.Pending, (await host.Job(unknownId)).State);
    }

    [Fact]
    public async Task A_Job_Restarted_Through_The_Database_Is_Re_Executed_With_A_Higher_Attempt_Count()
    {
        await using var host = JobTestHost.Create(Db);
        var id = await host.InsertRawJob(JobType, Payload("a"));

        await host.Drain();
        await host.SetState(id, JobState.Pending);
        await host.Drain();

        Assert.Equal(new[] { "a", "a" }, host.Log.Executed);
        var job = await host.Job(id);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public async Task An_Orphaned_Running_Row_Is_Not_Picked_Up()
    {
        await using var host = JobTestHost.Create(Db);
        await host.InsertRawJob(JobType, Payload("a"), JobState.Running);

        await host.Drain();

        Assert.Empty(host.Log.Executed);
    }

    [Fact]
    public async Task A_Transient_Handler_Failure_Is_Retried_When_The_Job_Opts_In()
    {
        await using var host = JobTestHost.Create(Db);
        host.Log.Behavior = (_, invocation) => invocation == 1
            ? throw new FakeTransientException()
            : Task.CompletedTask;
        var id = await host.InsertRawJob(typeof(RetryableTestJob).FullName!, Payload("r"));

        await host.Drain();

        Assert.Equal(2, host.Log.InvocationsOf("r"));
        Assert.Equal(JobState.Completed, (await host.Job(id)).State);
        // Only the successful attempt's write survived
        Assert.Equal(new[] { "r" }, await ReadItemNames());
    }

    [Fact]
    public async Task A_Transient_Handler_Failure_Fails_The_Job_Without_The_Opt_In()
    {
        await using var host = JobTestHost.Create(Db);
        host.Log.Behavior = (_, invocation) => invocation == 1
            ? throw new FakeTransientException()
            : Task.CompletedTask;
        var id = await host.InsertRawJob(JobType, Payload("a"));

        await host.Drain();

        Assert.Equal(1, host.Log.InvocationsOf("a"));
        Assert.Equal(JobState.Failed, (await host.Job(id)).State);
    }

    [Fact]
    public async Task Completed_Jobs_Are_Deleted_Once_The_Retention_Period_Passes()
    {
        await using var host = JobTestHost.Create(Db,
            configureJobs: o => o.CompletedJobRetention = TimeSpan.FromMilliseconds(50));
        await host.InsertRawJob(JobType, Payload("a"));

        await host.Drain();
        Assert.Single(await host.Jobs());

        await Task.Delay(150);
        await host.Drain();
        Assert.Empty(await host.Jobs());
    }

    [Fact]
    public async Task Completed_Jobs_Are_Kept_When_Retention_Is_Null()
    {
        await using var host = JobTestHost.Create(Db, configureJobs: o => o.CompletedJobRetention = null);
        await host.InsertRawJob(JobType, Payload("a"));

        await host.Drain();
        await Task.Delay(50);
        await host.Drain();

        var job = Assert.Single(await host.Jobs());
        Assert.Equal(JobState.Completed, job.State);
    }

    [Fact]
    public async Task Failed_Jobs_Are_Never_Deleted()
    {
        await using var host = JobTestHost.Create(Db,
            configureJobs: o => o.CompletedJobRetention = TimeSpan.FromMilliseconds(1));
        host.Log.Behavior = (_, _) => throw new FakePermanentException();
        await host.InsertRawJob(JobType, Payload("a"));

        await host.Drain();
        await Task.Delay(50);
        await host.Drain();

        var job = Assert.Single(await host.Jobs());
        Assert.Equal(JobState.Failed, job.State);
    }

    [Fact]
    public async Task The_Poller_Waits_For_The_Database_Ready_Signal()
    {
        await using var host = JobTestHost.Create(Db, databaseReady: false);
        await host.InsertRawJob(JobType, Payload("a"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Drain(cts.Token));
        Assert.Empty(host.Log.Executed);
        Assert.Equal(JobState.Pending, Assert.Single(await host.Jobs()).State);

        host.Ready.Set();
        await host.Drain();
        Assert.Equal(new[] { "a" }, host.Log.Executed);
    }

    [Fact]
    public void A_Ready_Signal_Constructed_Open_Is_Immediately_Complete()
    {
        Assert.True(new Pechka.AspNet.Database.DatabaseReadySignal(true).Ready.IsCompleted);
        Assert.False(new Pechka.AspNet.Database.DatabaseReadySignal(false).Ready.IsCompleted);
    }

    [Fact]
    public async Task Job_Handlers_Run_In_Their_Own_Di_Scope()
    {
        await using var host = JobTestHost.Create(Db);
        await host.InsertRawJob(JobType, Payload("a"));
        await host.InsertRawJob(JobType, Payload("b"));

        await host.Drain();

        // A single shared scope would have reused one manager whose completed root scope
        // would break the second job; both completing proves each got a fresh scope
        Assert.All(await host.Jobs(), j => Assert.Equal(JobState.Completed, j.State));
        Assert.Equal(new[] { "a", "b" }, await ReadItemNames());
    }
}
