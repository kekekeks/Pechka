using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pechka.AspNet.BackgroundServices;

namespace Pechka.AspNet.Tests;

/// <summary>
/// The only <see cref="TickingServiceBase"/> in this assembly; ServiceRunnerRegistry discovers it by
/// scanning, so keep it the single one (other test workers derive from TickingServiceWorkerBase).
/// </summary>
public sealed class TestTickingService : TickingServiceBase
{
    public static TaskCompletionSource? Gate;
    private static int _runs;

    public static int Runs => Volatile.Read(ref _runs);

    public static void Reset()
    {
        Gate = null;
        Volatile.Write(ref _runs, 0);
    }

    protected override async Task Run(CancellationToken token)
    {
        Interlocked.Increment(ref _runs);
        // Deliberately ignores the token so shutdown has to time out
        if (Gate != null)
            await Gate.Task;
    }
}

public class TickingServiceManagerTests : IDisposable
{
    private const string TimeoutWarning = "Some ticking services did not stop within the shutdown timeout";

    private readonly ListLoggerFactory _loggerFactory = new();
    private readonly FakeApplicationLifetime _lifetime = new();

    public TickingServiceManagerTests() => TestTickingService.Reset();

    public void Dispose()
    {
        TestTickingService.Gate?.TrySetResult();
        _lifetime.StopApplication();
        TestTickingService.Reset();
    }

    private (TickingServiceManager Manager, ServiceProvider Services) Build(PechkaBackgroundOptions? options = null)
    {
        var registry = new ServiceRunnerRegistry(typeof(TestTickingService).Assembly);
        var services = new ServiceCollection();
        registry.Register(services);
        services.AddSingleton<IHostApplicationLifetime>(_lifetime);
        services.AddSingleton<ILoggerFactory>(_loggerFactory);
        if (options != null)
            services.AddSingleton(options);
        var provider = services.BuildServiceProvider();
        return (new TickingServiceManager(registry, provider), provider);
    }

    [Fact]
    public void The_Registry_Discovers_Ticking_Services_In_The_Scanned_Assembly()
    {
        var registry = new ServiceRunnerRegistry(typeof(TestTickingService).Assembly);
        Assert.Contains(typeof(TestTickingService), registry.ServiceTypes);
        Assert.DoesNotContain(typeof(TickingServiceBase), registry.ServiceTypes);
    }

    [Fact]
    public async Task SyncAllServices_Runs_Every_Service_Once()
    {
        var (manager, services) = Build();
        await using var _ = services;

        await manager.SyncAllServices(CancellationToken.None);

        Assert.Equal(1, TestTickingService.Runs);
    }

    [Fact]
    public async Task Disabled_AutoStart_Keeps_Services_Idle_But_Manually_Syncable()
    {
        var (manager, services) = Build(new PechkaBackgroundOptions { AutoStart = false });
        await using var _ = services;

        await manager.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(0, TestTickingService.Runs);

        await manager.SyncAllServices(CancellationToken.None);
        Assert.Equal(1, TestTickingService.Runs);

        await manager.StopAsync(CancellationToken.None).WaitAsync(Poll.DefaultTimeout);
        Assert.DoesNotContain(_loggerFactory.Entries, e => e.Message == TimeoutWarning);
    }

    [Fact]
    public void DisablePechkaBackgroundAutoStart_Is_Idempotent_And_Flips_An_Existing_Instance()
    {
        var services = new ServiceCollection();
        var options = new PechkaBackgroundOptions();
        services.AddSingleton(options);

        services.DisablePechkaBackgroundAutoStart();
        services.DisablePechkaBackgroundAutoStart();

        Assert.False(options.AutoStart);
        Assert.Single(services, d => d.ServiceType == typeof(PechkaBackgroundOptions));

        var fresh = new ServiceCollection();
        fresh.DisablePechkaBackgroundAutoStart();
        var descriptor = Assert.Single(fresh, d => d.ServiceType == typeof(PechkaBackgroundOptions));
        Assert.False(((PechkaBackgroundOptions)descriptor.ImplementationInstance!).AutoStart);
    }

    [Fact]
    public async Task StopAsync_Abandons_A_Service_That_Ignores_Cancellation_And_Logs_A_Warning()
    {
        TestTickingService.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (manager, services) = Build();
        await using var _ = services;

        await manager.StartAsync(CancellationToken.None);
        await Poll.Until(() => TestTickingService.Runs >= 1, "the service to start ticking");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await manager.StopAsync(cts.Token).WaitAsync(Poll.DefaultTimeout);

        var warning = Assert.Single(_loggerFactory.Entries, e => e.Message == TimeoutWarning);
        Assert.Equal(LogLevel.Warning, warning.Level);
    }

    [Fact]
    public async Task StopAsync_Awaits_A_Service_That_Honors_Cancellation()
    {
        var (manager, services) = Build();
        await using var _ = services;
        var service = services.GetRequiredService<TestTickingService>();

        await manager.StartAsync(CancellationToken.None);
        await Poll.Until(() => TestTickingService.Runs >= 1, "the service to start ticking");
        _lifetime.StopApplication();

        await manager.StopAsync(CancellationToken.None).WaitAsync(Poll.DefaultTimeout);

        Assert.True(service.Completion.IsCompleted);
        Assert.DoesNotContain(_loggerFactory.Entries, e => e.Message == TimeoutWarning);
    }
}
