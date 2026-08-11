using CoreRPC.AspNetCore;
using LinqToDB;
using LinqToDB.Async;
using Pechka.AspNet.Database;

namespace MyWebApp;

public class TodoRpc : IHttpContextAwareRpc
{
    private readonly MyDbContextManager _db;

    public TodoRpc(MyDbContextManager db) => _db = db;

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
}
