using Microsoft.Extensions.Logging.Abstractions;
using Pechka.AspNet.BackgroundServices;

namespace Pechka.AspNet.Tests;

/// <summary>
/// Deriving from <see cref="TickingServiceWorkerBase"/> (not <see cref="TickingServiceBase"/>) keeps this
/// out of the assembly scan done by ServiceRunnerRegistry in TickingServiceManagerTests.
/// </summary>
public sealed class CountingWorker : TickingServiceWorkerBase
{
    private int _runs;
    private int _cleanups;

    public Func<CancellationToken, Task>? Body { get; set; }
    public int Runs => Volatile.Read(ref _runs);
    public int Cleanups => Volatile.Read(ref _cleanups);

    public void SetInterval(TimeSpan value) => Interval = value;

    protected override async Task Run(CancellationToken token)
    {
        Interlocked.Increment(ref _runs);
        if (Body != null)
            await Body(token);
    }

    protected override Task Cleanup()
    {
        Interlocked.Increment(ref _cleanups);
        return Task.CompletedTask;
    }
}

public class TickingServiceTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        TickingServiceWorkerBase.IntervalOverride = null;
        _cts.Cancel();
        _cts.Dispose();
    }

    private CountingWorker Start(TimeSpan? intervalOverride = null, TimeSpan? interval = null)
    {
        TickingServiceWorkerBase.IntervalOverride = intervalOverride;
        var worker = new CountingWorker();
        if (interval != null)
            worker.SetInterval(interval.Value);
        worker.Start(_cts.Token, NullLoggerFactory.Instance);
        return worker;
    }

    [Fact]
    public async Task Start_Ticks_Repeatedly()
    {
        var worker = Start(intervalOverride: TimeSpan.FromMilliseconds(5));
        await Poll.Until(() => worker.Runs >= 3, "three ticks");
        _cts.Cancel();
        await worker.Completion;
    }

    [Fact]
    public async Task ExpediteTick_Short_Circuits_The_Interval_Wait()
    {
        var worker = Start(interval: TimeSpan.FromMinutes(5));
        await Poll.Until(() => worker.Runs >= 1, "the first tick");
        worker.ExpediteTick();
        await Poll.Until(() => worker.Runs >= 2, "the expedited tick");
        _cts.Cancel();
        await worker.Completion;
    }

    [Fact]
    public async Task An_Exception_In_Run_Does_Not_Stop_The_Loop()
    {
        TickingServiceWorkerBase.IntervalOverride = TimeSpan.FromMilliseconds(5);
        var worker = new CountingWorker();
        worker.Body = _ => worker.Runs == 1
            ? throw new FakePermanentException()
            : Task.CompletedTask;
        worker.Start(_cts.Token, NullLoggerFactory.Instance);

        await Poll.Until(() => worker.Runs >= 3, "the loop to keep ticking after a failure");
        _cts.Cancel();
        await worker.Completion;
    }

    [Fact]
    public async Task Cleanup_Runs_Exactly_Once_After_Cancellation()
    {
        var worker = Start(intervalOverride: TimeSpan.FromMilliseconds(5));
        await Poll.Until(() => worker.Runs >= 1, "the first tick");
        _cts.Cancel();
        await worker.Completion;
        Assert.Equal(1, worker.Cleanups);
    }

    [Fact]
    public async Task Cleanup_Runs_When_Cancellation_Lands_During_The_Interval_Delay()
    {
        var worker = Start(intervalOverride: TimeSpan.FromMinutes(5));
        await Poll.Until(() => worker.Runs >= 1, "the first tick");
        // The worker is now parked in the (very long) interval delay
        _cts.Cancel();
        await worker.Completion.WaitAsync(Poll.DefaultTimeout);
        Assert.Equal(1, worker.Cleanups);
        Assert.Equal(1, worker.Runs);
    }

    [Fact]
    public async Task A_Run_That_Ignores_The_Token_Leaves_Completion_Incomplete()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TickingServiceWorkerBase.IntervalOverride = TimeSpan.FromMilliseconds(5);
        var worker = new CountingWorker { Body = _ => gate.Task };
        worker.Start(_cts.Token, NullLoggerFactory.Instance);
        await Poll.Until(() => worker.Runs >= 1, "the first tick");

        _cts.Cancel();
        await Task.Delay(200);
        Assert.False(worker.Completion.IsCompleted);
        Assert.Equal(0, worker.Cleanups);

        gate.SetResult();
        await worker.Completion.WaitAsync(Poll.DefaultTimeout);
        Assert.Equal(1, worker.Cleanups);
    }

    [Fact]
    public async Task ForceSync_Runs_The_Body_Exactly_Once()
    {
        var worker = new CountingWorker();
        await worker.ForceSync(CancellationToken.None);
        Assert.Equal(1, worker.Runs);
        Assert.Equal(0, worker.Cleanups);
        Assert.True(worker.Completion.IsCompleted);
    }
}
