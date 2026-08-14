using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pechka.AspNet.BackgroundServices;

namespace Pechka.AspNet.Tests;

internal sealed class FakeBackgroundWorker : IPechkaBackgroundWorker
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Starts { get; private set; }

    public void Start(IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) => Starts++;

    public Task Completion => _tcs.Task;

    public void Finish() => _tcs.TrySetResult();
}

public class BackgroundWorkersRunnerTests
{
    private const string TimeoutWarning = "Some background workers did not stop within the shutdown timeout";

    private static PechkaBackgroundWorkersRunner CreateRunner(FakeBackgroundWorker[] workers,
        ListLoggerFactory logger, PechkaBackgroundOptions? options = null)
    {
        var services = new ServiceCollection();
        if (options != null)
            services.AddSingleton(options);
        return new PechkaBackgroundWorkersRunner(workers, new FakeApplicationLifetime(), logger,
            services.BuildServiceProvider());
    }

    [Fact]
    public async Task StartAsync_Starts_Every_Registered_Worker()
    {
        var workers = new[] { new FakeBackgroundWorker(), new FakeBackgroundWorker() };
        var runner = CreateRunner(workers, new ListLoggerFactory());

        await runner.StartAsync(CancellationToken.None);

        Assert.All(workers, w => Assert.Equal(1, w.Starts));
    }

    [Fact]
    public async Task StartAsync_Skips_Workers_When_AutoStart_Is_Disabled()
    {
        var workers = new[] { new FakeBackgroundWorker() };
        var runner = CreateRunner(workers, new ListLoggerFactory(),
            new PechkaBackgroundOptions { AutoStart = false });

        await runner.StartAsync(CancellationToken.None);
        await runner.StopAsync(CancellationToken.None).WaitAsync(Poll.DefaultTimeout);

        Assert.Equal(0, workers[0].Starts);
        workers[0].Finish();
    }

    [Fact]
    public async Task StartAsync_Starts_Workers_When_AutoStart_Is_Explicitly_True()
    {
        var workers = new[] { new FakeBackgroundWorker() };
        var runner = CreateRunner(workers, new ListLoggerFactory(), new PechkaBackgroundOptions());

        await runner.StartAsync(CancellationToken.None);

        Assert.Equal(1, workers[0].Starts);
    }

    [Fact]
    public async Task StopAsync_Awaits_Workers_That_Complete()
    {
        var workers = new[] { new FakeBackgroundWorker(), new FakeBackgroundWorker() };
        var logger = new ListLoggerFactory();
        var runner = CreateRunner(workers, logger);
        await runner.StartAsync(CancellationToken.None);

        foreach (var worker in workers)
            worker.Finish();
        await runner.StopAsync(CancellationToken.None).WaitAsync(Poll.DefaultTimeout);

        Assert.DoesNotContain(logger.Entries, e => e.Message == TimeoutWarning);
    }

    [Fact]
    public async Task StopAsync_Abandons_A_Hanging_Worker_And_Logs_A_Warning()
    {
        var hanging = new FakeBackgroundWorker();
        var logger = new ListLoggerFactory();
        var runner = CreateRunner(new[] { hanging }, logger);
        await runner.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await runner.StopAsync(cts.Token).WaitAsync(Poll.DefaultTimeout);

        var warning = Assert.Single(logger.Entries, e => e.Message == TimeoutWarning);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.False(hanging.Completion.IsCompleted);
        hanging.Finish();
    }

    [Fact]
    public async Task StopAsync_With_An_Already_Cancelled_Token_Returns_Promptly()
    {
        var hanging = new FakeBackgroundWorker();
        var logger = new ListLoggerFactory();
        var runner = CreateRunner(new[] { hanging }, logger);
        await runner.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await runner.StopAsync(cts.Token).WaitAsync(Poll.DefaultTimeout);

        Assert.Contains(logger.Entries, e => e.Message == TimeoutWarning);
        hanging.Finish();
    }
}
