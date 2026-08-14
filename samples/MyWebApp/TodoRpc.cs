using System.Data;
using CoreRPC.AspNetCore;
using LinqToDB;
using LinqToDB.Async;
using Pechka.AspNet.Database;
using Pechka.AspNet.Jobs;

namespace MyWebApp;

public class TodoRpc : IHttpContextAwareRpc
{
    private readonly MyDbContextManager _db;
    private readonly IBackgroundJobScheduler _jobs;

    public TodoRpc(MyDbContextManager db, IBackgroundJobScheduler jobs)
    {
        _db = db;
        _jobs = jobs;
    }

    Task<object> IHttpContextAwareRpc.OnExecuteRpcCall(HttpContext context, Func<Task<object>> action)
        => action();

    // Two independent Exec calls, atomic thanks to the implicit per-call transaction scope
    public async Task<int> AddPair(string first, string second)
    {
        var id = await _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = first }));
        await _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = second }));
        return id;
    }

    // The first insert is rolled back because the call throws before completing
    public async Task<int> AddPairFailing(string first)
    {
        await _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = first }));
        throw new InvalidOperationException("Intentional failure, the insert above must be rolled back");
    }

    // Explicit scope: joins the implicit one as a nested scope, commit is a vote
    public async Task<int> AddExplicit(string name)
    {
        await using var tx = _db.BeginTransaction();
        var id = await _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = name }));
        await tx.CommitAsync();
        return id;
    }

    // Parallel Execs are legal inside a scope; they share one connection and get serialized
    public async Task<int> AddParallel(string first, string second)
    {
        var ids = await Task.WhenAll(
            _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = first })),
            _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = second })));
        return ids[0];
    }

    [NoTransaction]
    public Task<TodoItem[]> List()
        => _db.ExecAsync(db => db.GetTable<TodoItem>().OrderBy(x => x.Id).ToArrayAsync());

    // The job row is part of the implicit per-call unit of work
    public Task<long> EnqueueGreeting(string name)
        => _jobs.Enqueue(new GreetingJob { Name = name });

    // The thrown exception rolls back the whole call, including the enqueued job
    public async Task<long> EnqueueGreetingFailing(string name)
    {
        await _jobs.Enqueue(new GreetingJob { Name = name });
        throw new InvalidOperationException("Intentional failure, the enqueue above must be rolled back");
    }

    public Task<long> EnqueueFlaky(string reason)
        => _jobs.Enqueue(new FlakyJob { Reason = reason });

    public Task<long> EnqueueTransient(string name)
        => _jobs.Enqueue(new TransientJob { Name = name });

    // First attempt hits a 40001 from the probe, the automatic retry succeeds
    public async Task<int> FlakyInsert(string name)
    {
        await RetryProbe.FailEveryOtherAttempt(_db);
        return await _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = name }));
    }

    [NoRetry]
    public async Task<int> FlakyInsertNoRetry(string name)
    {
        await RetryProbe.FailEveryOtherAttempt(_db);
        return await _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = name }));
    }

    // Serializable read-modify-write on a shared row ("counter-N" TodoItem); concurrent calls
    // conflict with a real 40001 and get transparently retried
    public Task<int> IncrementCounter() => _db.WithTransaction(async db =>
    {
        var row = await db.GetTable<TodoItem>().Where(x => x.Name.StartsWith("counter-")).FirstAsync();
        var value = int.Parse(row.Name.Substring("counter-".Length)) + 1;
        await Task.Delay(300);
        await db.GetTable<TodoItem>().Where(x => x.Id == row.Id)
            .Set(x => x.Name, $"counter-{value}")
            .UpdateAsync();
        return value;
    }, IsolationLevel.Serializable);
}
