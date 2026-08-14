using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pechka.AspNet.BackgroundServices;

/// <summary>
/// A framework-provided background worker (e.g. the job queue poller). Registering an
/// implementation only declares intent; workers are actually started by
/// <see cref="PechkaBackgroundWorkersRunner"/>, hosted under the "services" role, so
/// cmdlet or web-only processes don't run them.
/// </summary>
internal interface IPechkaBackgroundWorker
{
    void Start(IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory);
    Task Completion { get; }
}

internal sealed class PechkaBackgroundWorkersRunner : IHostedService
{
    private readonly IEnumerable<IPechkaBackgroundWorker> _workers;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private bool _started;

    public PechkaBackgroundWorkersRunner(IEnumerable<IPechkaBackgroundWorker> workers,
        IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _workers = workers;
        _lifetime = lifetime;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_serviceProvider.GetService<PechkaBackgroundOptions>()?.AutoStart == false)
            return Task.CompletedTask;
        _started = true;
        foreach (var worker in _workers)
            worker.Start(_lifetime, _loggerFactory);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
            return;
        try
        {
            await Task.WhenAll(_workers.Select(w => w.Completion)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _loggerFactory.CreateLogger<PechkaBackgroundWorkersRunner>()
                .LogWarning("Some background workers did not stop within the shutdown timeout");
        }
    }
}
