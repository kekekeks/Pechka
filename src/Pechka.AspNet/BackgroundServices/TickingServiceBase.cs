using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;

namespace Pechka.AspNet.BackgroundServices;

public abstract class TickingServiceBase : TickingServiceWorkerBase
{
    
}

public abstract class TickingServiceWorkerBase
{
    private readonly AsyncLock _lock = new();

    public static TimeSpan? IntervalOverride { get; set; }
    protected TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
    private CancellationTokenSource? _currentTickWait;
    private bool _tickExpedited;
    private object _currentTickWaitLock = new();

    public void Start(IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory)
    {
        Start(lifetime.ApplicationStopping, loggerFactory);
    }

    /// <summary>
    /// Completes when the worker loop (including <see cref="Cleanup"/>) has finished after
    /// cancellation. Hosted-service wrappers await this in StopAsync with the host's shutdown
    /// token, so a worker that ignores cancellation is abandoned instead of hanging shutdown.
    /// </summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Start(CancellationToken cancel, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(GetType());
        Completion = Task.Run(async () =>
        {
            try
            {
                await RunLoop(cancel, logger);
            }
            finally
            {
                try
                {
                    await Cleanup();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error during service cleanup");
                }
            }
        });
    }

    private async Task RunLoop(CancellationToken cancel, ILogger logger)
    {
        while (!cancel.IsCancellationRequested)
            try
            {
                using (await _lock.LockAsync(cancel))
                {
                    await Run(cancel);
                }

                bool expedited;
                lock (_currentTickWaitLock)
                {
                    _currentTickWait = new CancellationTokenSource();
                    expedited = _tickExpedited;
                    _tickExpedited = false;
                }
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _currentTickWait.Token);
                    if (!expedited)
                        await Task.Delay(IntervalOverride ?? Interval, linked.Token);
                }
                catch (OperationCanceledException)
                {

                }
                finally
                {
                    lock (_currentTickWaitLock)
                    {
                        _currentTickWait?.Dispose();
                        _currentTickWait = null;
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error in a service");
                try
                {
                    await Task.Delay(IntervalOverride ?? TimeSpan.FromMinutes(1), cancel);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
    }

    public async Task ForceSync(CancellationToken token)
    {
        using (await _lock.LockAsync(token))
        {
            await Run(token);
        }
    }

    public void ExpediteTick()
    {
        lock (_currentTickWaitLock)
        {
            _tickExpedited = true;
            _currentTickWait?.Cancel();
        }
    }

    protected abstract Task Run(CancellationToken token);

    protected virtual Task Cleanup()
    {
        return Task.CompletedTask;
    }
}