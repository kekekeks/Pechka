using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pechka.AspNet.BackgroundServices;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Jobs;

/// <summary>
/// Single worker draining the job queue in FIFO (Id) order. A job is claimed by a CAS update to
/// Running outside of any transaction, then its handler runs in a fresh DI scope wrapped in a
/// unit of work; completion is committed atomically with the handler's writes. Failed jobs are
/// skipped (queue continues) and can be restarted by setting State back to 0 (Pending) in the
/// database; the same applies to Running rows orphaned by a crash.
/// </summary>
internal sealed class BackgroundJobPoller<TContextManager> : TickingServiceWorkerBase, IHostedService
    where TContextManager : class, ITransactionalDbContextManager, IUntypedDbContextManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundJobRegistry _registry;
    private readonly PechkaBackgroundJobOptions _options;
    private readonly PechkaDbTransactionOptions _txOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public BackgroundJobPoller(IServiceScopeFactory scopeFactory, BackgroundJobRegistry registry,
        PechkaBackgroundJobOptions options, IServiceProvider serviceProvider,
        IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _options = options;
        _txOptions = serviceProvider.GetService<PechkaDbTransactionOptions>() ?? new PechkaDbTransactionOptions();
        _lifetime = lifetime;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger(GetType());
        Interval = options.PollingInterval;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start(_lifetime, _loggerFactory);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var manager = scope.ServiceProvider.GetRequiredService<TContextManager>();

            var row = await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .Where(x => x.State == JobState.Pending)
                .OrderBy(x => x.Id)
                .FirstOrDefaultAsync(token));
            if (row == null)
                break;

            // Claim outside of any transaction; CAS guards against a concurrent app instance
            var claimed = await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .Where(x => x.Id == row.Id && x.State == JobState.Pending)
                .Set(x => x.State, JobState.Running)
                .Set(x => x.TakenAt, JobTime.UtcNow)
                .Set(x => x.Attempts, x => x.Attempts + 1)
                .UpdateAsync(token));
            if (claimed == 0)
                continue;

            await ExecuteJob(scope.ServiceProvider, manager, row, token);
        }

        await CleanupCompleted(token);
    }

    private async Task ExecuteJob(IServiceProvider services, TContextManager manager, PechkaJobRow row,
        CancellationToken token)
    {
        var registration = _registry.TryGetByIdentifier(row.Type);
        if (registration == null)
        {
            await MarkFailed(manager, row.Id, $"No handler registered for job type '{row.Type}'");
            return;
        }
        try
        {
            var managers = services.GetServices<ITransactionalDbContextManager>().ToList();
            await using (var scopes = TransactionScopeSet.Begin(managers, _txOptions, _logger,
                             $"background job {row.Type}#{row.Id}"))
            {
                await registration.Invoke(services, row.Payload, token);
                // Joins the unit of work, so completion is atomic with the handler's writes
                await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                    .Where(x => x.Id == row.Id)
                    .Set(x => x.State, JobState.Completed)
                    .Set(x => x.FinishedAt, JobTime.UtcNow)
                    .UpdateAsync(token));
                await scopes.CommitAsync();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutdown: the handler's transaction was rolled back, so put the job back in the queue
            try
            {
                await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                    .Where(x => x.Id == row.Id && x.State == JobState.Running)
                    .Set(x => x.State, JobState.Pending)
                    .UpdateAsync(CancellationToken.None));
            }
            catch
            {
                // Best effort; an orphaned Running row is restartable via the database
            }
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Background job {Type}#{Id} failed", row.Type, row.Id);
            await MarkFailed(manager, row.Id, e.ToString());
        }
    }

    private Task<int> MarkFailed(TContextManager manager, long id, string error)
        => manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
            .Where(x => x.Id == id)
            .Set(x => x.State, JobState.Failed)
            .Set(x => x.FinishedAt, JobTime.UtcNow)
            .Set(x => x.Error, error)
            .UpdateAsync(CancellationToken.None));

    private async Task CleanupCompleted(CancellationToken token)
    {
        if (_options.CompletedJobRetention is not { } retention)
            return;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TContextManager>();
        var cutoff = JobTime.UtcNow - retention;
        await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
            .Where(x => x.State == JobState.Completed && x.FinishedAt < cutoff)
            .DeleteAsync(token));
    }
}
