using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;

namespace Pechka.AspNet.BackgroundServices;

public abstract class TickingServiceBase
{
    private readonly AsyncLock _lock = new();

    public static TimeSpan? IntervalOverride { get; set; }
    protected TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

    public void Start(IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        var task = Task.Run(async () =>
        {
            while (!lifetime.ApplicationStopping.IsCancellationRequested)
                try
                {
                    using (await _lock.LockAsync(lifetime.ApplicationStopping))
                    {
                        await Run(lifetime.ApplicationStopping);
                    }

                    await Task.Delay(IntervalOverride ?? Interval, lifetime.ApplicationStopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error in a service");
                    await Task.Delay(IntervalOverride ?? TimeSpan.FromMinutes(1), lifetime.ApplicationStopping);
                }
        });
        lifetime.ApplicationStopped.Register(() => task.Wait());
    }

    public async Task ForceSync(CancellationToken token)
    {
        using (await _lock.LockAsync(token))
        {
            await Run(token);
        }
    }

    protected abstract Task Run(CancellationToken token);
}