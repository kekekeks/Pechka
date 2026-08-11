using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;
using Pechka.AspNet.Database;

namespace MyWebApp;

[ApiController]
[Route("api/todo")]
public class TodoController : ControllerBase
{
    private readonly MyDbContextManager _db;

    public TodoController(MyDbContextManager db) => _db = db;

    [HttpPost("pair")]
    public async Task<int> AddPair(string first, string second)
    {
        var id = await _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = first }));
        await _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = second }));
        return id;
    }

    [HttpPost("pair-failing")]
    public async Task<int> AddPairFailing(string first)
    {
        await _db.ExecAsync(db => db.InsertWithInt32IdentityAsync(new TodoItem { Name = first }));
        throw new InvalidOperationException("Intentional failure, the insert above must be rolled back");
    }

    [HttpGet]
    [NoTransaction]
    public Task<TodoItem[]> List()
        => _db.ExecAsync(db => db.GetTable<TodoItem>().OrderBy(x => x.Id).ToArrayAsync());
}
