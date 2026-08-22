using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pechka.AspNet.BackgroundServices;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Jobs;

/// <summary>
/// Single worker draining the job queue in FIFO (Id) order. A job is claimed by a CAS update to
/// Running outside of any transaction, then its handler runs in a fresh DI scope wrapped in a
/// unit of work; completion is committed atomically with the handler's writes. Failed jobs are
/// skipped (queue continues) and can be restarted by setting State back to 0 (Pending) in the
/// database; the same applies to Running rows orphaned by a crash (when restarting an expired
/// job, also clear ExpiresAt or it expires again immediately). Jobs whose identifier has no
/// handler in this process are never claimed — a node that knows them (e.g. a newer deployment)
/// picks them up instead. Pending jobs past their ExpiresAt are auto-failed by the maintenance
/// sweep; terminal rows older than the configured retentions are deleted by it.
/// </summary>
internal sealed class BackgroundJobPoller<TContextManager> : TickingServiceWorkerBase, IPechkaBackgroundWorker,
    IBackgroundJobDispatcher
    where TContextManager : class, ITransactionalDbContextManager, IUntypedDbContextManager
{
    private int _processedCounter;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundJobRegistry _registry;
    private readonly PechkaBackgroundJobOptions _options;
    private readonly PechkaDbTransactionOptions _txOptions;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Task _databaseReady;

    public BackgroundJobPoller(IServiceScopeFactory scopeFactory, BackgroundJobRegistry registry,
        PechkaBackgroundJobOptions options, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _options = options;
        _txOptions = serviceProvider.GetService<PechkaDbTransactionOptions>() ?? new PechkaDbTransactionOptions();
        _time = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        _databaseReady = serviceProvider.GetService<DatabaseReadySignal>()?.Ready ?? Task.CompletedTask;
        _logger = loggerFactory.CreateLogger(GetType());
        Interval = options.PollingInterval;
    }

    public async Task<int> RunPendingJobsAsync(CancellationToken token = default)
    {
        var before = Volatile.Read(ref _processedCounter);
        await ForceSync(token);
        return Volatile.Read(ref _processedCounter) - before;
    }

    protected override async Task Run(CancellationToken token)
    {
        // Hosted services can start ticking before the startup migrations have created the
        // job table; polling it too early would trip the worker's one-minute error backoff
        await _databaseReady.WaitAsync(token);

        while (!token.IsCancellationRequested)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var manager = scope.ServiceProvider.GetRequiredService<TContextManager>();

            // Jobs with identifiers this process doesn't know (e.g. a newer node's job types
            // during a rolling deploy) are left Pending for a node that has the handler;
            // expired jobs are left for the maintenance sweep to fail
            var known = _registry.KnownIdentifiers;
            var now = JobTime.From(_time);
            var row = await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .Where(x => x.State == JobState.Pending && known.Contains(x.Type)
                            && (x.ExpiresAt == null || x.ExpiresAt > now))
                .OrderBy(x => x.Id)
                .FirstOrDefaultAsync(token));
            if (row == null)
                break;

            // Claim outside of any transaction; CAS guards against a concurrent app instance
            var claimed = await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .Where(x => x.Id == row.Id && x.State == JobState.Pending)
                .Set(x => x.State, JobState.Running)
                .Set(x => x.TakenAt, JobTime.From(_time))
                .Set(x => x.Attempts, x => x.Attempts + 1)
                .UpdateAsync(token));
            if (claimed == 0)
                continue;
            Interlocked.Increment(ref _processedCounter);

            await ExecuteJob(scope.ServiceProvider, manager, row, token);
        }

        await RunMaintenance(token);
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
        var operationName = $"background job {row.Type}#{row.Id}";
        async Task<object?> Attempt()
        {
            var managers = services.GetServices<ITransactionalDbContextManager>().ToList();
            await using var scopes = TransactionScopeSet.Begin(managers, _txOptions, _logger, operationName);
            await registration.Invoke(services, row.Payload, token);
            // Joins the unit of work, so completion is atomic with the handler's writes
            await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .Where(x => x.Id == row.Id)
                .Set(x => x.State, JobState.Completed)
                .Set(x => x.FinishedAt, JobTime.From(_time))
                .UpdateAsync(token));
            await scopes.CommitAsync();
            return null;
        }

        try
        {
            if (registration.RetryTransientFailures)
                await TransactionRetry.ExecuteAsync(_txOptions, _logger, operationName,
                    _ => Attempt(), token: token);
            else
                await Attempt();
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
            .Set(x => x.FinishedAt, JobTime.From(_time))
            .Set(x => x.Error, error)
            .UpdateAsync(CancellationToken.None));

    private async Task RunMaintenance(CancellationToken token)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<TContextManager>();
        var now = JobTime.From(_time);

        // Each write is guarded by an existence check so an idle maintenance pass stays read-only
        if (await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .AnyAsync(x => x.State == JobState.Pending && x.ExpiresAt < now, token)))
            await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .Where(x => x.State == JobState.Pending && x.ExpiresAt < now)
                .Set(x => x.State, JobState.Failed)
                .Set(x => x.FinishedAt, now)
                .Set(x => x.Error, "Job expired before execution")
                .UpdateAsync(token));

        if (_options.CompletedJobRetention is { } completedRetention
            && await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .AnyAsync(x => x.State == JobState.Completed && x.FinishedAt < now - completedRetention, token)))
            await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .Where(x => x.State == JobState.Completed && x.FinishedAt < now - completedRetention)
                .DeleteAsync(token));

        if (_options.StaleJobRetention is { } staleRetention
            && await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .AnyAsync(x => (x.State == JobState.Completed || x.State == JobState.Failed)
                               && x.FinishedAt < now - staleRetention, token)))
            await manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
                .Where(x => (x.State == JobState.Completed || x.State == JobState.Failed)
                            && x.FinishedAt < now - staleRetention)
                .DeleteAsync(token));
    }
}
