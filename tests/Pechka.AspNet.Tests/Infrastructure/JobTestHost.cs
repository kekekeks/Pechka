using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pechka.AspNet.Database;
using Pechka.AspNet.Jobs;

namespace Pechka.AspNet.Tests.Infrastructure;

public class TestJob
{
    public string Name { get; set; } = null!;
}

public class RetryableTestJob
{
    public string Name { get; set; } = null!;
}

public class ExpiringTestJob
{
    public string Name { get; set; } = null!;
}

/// <summary>Shared observation point for the test job handlers.</summary>
public sealed class JobLog
{
    private readonly List<string> _executed = new();
    private readonly Dictionary<string, int> _invocations = new();
    private readonly List<ClaimSnapshot> _claims = new();

    /// <summary>Called at the end of every handler run with the job name and its 1-based invocation number.</summary>
    public Func<string, int, Task>? Behavior { get; set; }

    public IReadOnlyList<string> Executed
    {
        get
        {
            lock (_executed)
                return _executed.ToList();
        }
    }

    public IReadOnlyList<ClaimSnapshot> Claims
    {
        get
        {
            lock (_executed)
                return _claims.ToList();
        }
    }

    public int InvocationsOf(string name)
    {
        lock (_executed)
            return _invocations.GetValueOrDefault(name);
    }

    public int Record(string name)
    {
        lock (_executed)
        {
            _executed.Add(name);
            return _invocations[name] = _invocations.GetValueOrDefault(name) + 1;
        }
    }

    public void RecordClaim(ClaimSnapshot snapshot)
    {
        lock (_executed)
            _claims.Add(snapshot);
    }

    public sealed record ClaimSnapshot(long Id, int State, int Attempts, bool TakenAtSet);
}

/// <summary>Body shared by the test job handlers; runs inside the poller's unit of work.</summary>
public sealed class TestJobExecutor
{
    private readonly JobLog _log;
    private readonly TestDbManager _manager;

    public TestJobExecutor(JobLog log, TestDbManager manager)
    {
        _log = log;
        _manager = manager;
    }

    public async Task Run(string name, CancellationToken token)
    {
        var invocation = _log.Record(name);
        // Observes the claim the poller committed before entering the handler
        var claimed = await _manager.ExecUntypedAsync(dc => dc.GetTable<PechkaJobRow>()
            .Where(x => x.State == JobState.Running).ToListAsync(token));
        foreach (var row in claimed)
            _log.RecordClaim(new JobLog.ClaimSnapshot(row.Id, row.State, row.Attempts, row.TakenAt != null));
        await _manager.ExecAsync(ctx => ctx.InsertAsync(new TestItem { Name = name }));
        if (_log.Behavior != null)
            await _log.Behavior(name, invocation);
    }
}

public sealed class TestJobHandler : IBackgroundJobHandler<TestJob>
{
    private readonly TestJobExecutor _executor;

    public TestJobHandler(TestJobExecutor executor) => _executor = executor;

    public Task Execute(TestJob job, CancellationToken token) => _executor.Run(job.Name, token);
}

public sealed class RetryableTestJobHandler : IBackgroundJobHandler<RetryableTestJob>
{
    private readonly TestJobExecutor _executor;

    public RetryableTestJobHandler(TestJobExecutor executor) => _executor = executor;

    public Task Execute(RetryableTestJob job, CancellationToken token) => _executor.Run(job.Name, token);
}

public sealed class ExpiringTestJobHandler : IBackgroundJobHandler<ExpiringTestJob>
{
    private readonly TestJobExecutor _executor;

    public ExpiringTestJobHandler(TestJobExecutor executor) => _executor = executor;

    public Task Execute(ExpiringTestJob job, CancellationToken token) => _executor.Run(job.Name, token);
}

internal sealed class JobTestHost : IAsyncDisposable
{
    private JobTestHost(ServiceProvider services, SqliteTestDatabase db, PechkaBackgroundJobOptions jobOptions,
        PechkaDbTransactionOptions txOptions, DatabaseReadySignal ready)
    {
        Services = services;
        Db = db;
        JobOptions = jobOptions;
        TxOptions = txOptions;
        Ready = ready;
        Poller = services.GetRequiredService<BackgroundJobPoller<TestDbManager>>();
        Log = services.GetRequiredService<JobLog>();
    }

    public ServiceProvider Services { get; }
    public SqliteTestDatabase Db { get; }
    public PechkaBackgroundJobOptions JobOptions { get; }
    public PechkaDbTransactionOptions TxOptions { get; }
    public DatabaseReadySignal Ready { get; }
    public BackgroundJobPoller<TestDbManager> Poller { get; }
    public JobLog Log { get; }

    public static JobTestHost Create(SqliteTestDatabase db, bool databaseReady = true,
        Action<PechkaBackgroundJobOptions>? configureJobs = null,
        Action<PechkaDbTransactionOptions>? configureTx = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => db.CreateManager());
        services.AddScoped<ITransactionalDbContextManager>(sp => sp.GetRequiredService<TestDbManager>());

        var txOptions = new PechkaDbTransactionOptions
        {
            RetryMaxAttempts = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            RetryMaxDelay = TimeSpan.FromMilliseconds(2),
            RetryBudgetMaxRetries = 100,
            RetryBudgetWindow = TimeSpan.FromMinutes(1),
            IsTransientFailure = e => e is FakeTransientException
        };
        configureTx?.Invoke(txOptions);
        services.AddSingleton(txOptions);

        var ready = new DatabaseReadySignal(databaseReady);
        services.AddSingleton(ready);
        services.AddSingleton<JobLog>();
        services.AddScoped<TestJobExecutor>();

        services.AddBackgroundJobs<TestDbManager>(configureJobs);
        services.AddBackgroundJob<TestJob, TestJobHandler>();
        services.AddBackgroundJob<RetryableTestJob, RetryableTestJobHandler>(retryTransientFailures: true);
        services.AddBackgroundJob<ExpiringTestJob, ExpiringTestJobHandler>(
            expiration: TimeSpan.FromMinutes(5));

        var provider = services.BuildServiceProvider();
        return new JobTestHost(provider, db, provider.GetRequiredService<PechkaBackgroundJobOptions>(),
            txOptions, ready);
    }

    public ILoggerFactory LoggerFactory => Services.GetRequiredService<ILoggerFactory>();

    /// <summary>Runs exactly one full queue drain.</summary>
    public Task Drain(CancellationToken token = default) => Poller.ForceSync(token);

    public async Task<List<PechkaJobRow>> Jobs()
    {
        await using var ctx = Db.CreateContext();
        return await ctx.GetTable<PechkaJobRow>().OrderBy(x => x.Id).ToListAsync();
    }

    public async Task<PechkaJobRow> Job(long id)
    {
        await using var ctx = Db.CreateContext();
        return await ctx.GetTable<PechkaJobRow>().FirstAsync(x => x.Id == id);
    }

    public async Task<long> InsertRawJob(string type, string? payload = null, int state = JobState.Pending,
        DateTime? expiresAt = null, DateTime? finishedAt = null)
    {
        await using var ctx = Db.CreateContext();
        return await ctx.InsertWithInt64IdentityAsync(new PechkaJobRow
        {
            Type = type,
            Payload = payload,
            State = state,
            CreatedAt = JobTime.UtcNow,
            ExpiresAt = expiresAt,
            FinishedAt = finishedAt
        });
    }

    public async Task SetState(long id, int state)
    {
        await using var ctx = Db.CreateContext();
        await ctx.GetTable<PechkaJobRow>().Where(x => x.Id == id).Set(x => x.State, state).UpdateAsync();
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
