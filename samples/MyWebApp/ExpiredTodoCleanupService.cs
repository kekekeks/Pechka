using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Pechka.AspNet.BackgroundServices;
using Pechka.AspNet.Database;

namespace MyWebApp;

// Ticking services stay singletons; a scoped transactional manager is obtained by creating
// a DI scope per tick, with a retryable transaction scope around the work.
public class ExpiredTodoCleanupService : TickingServiceBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRetryableTransactionRunner _runner;

    public ExpiredTodoCleanupService(IServiceScopeFactory scopeFactory, IRetryableTransactionRunner runner)
    {
        _scopeFactory = scopeFactory;
        _runner = runner;
        Interval = TimeSpan.FromSeconds(5);
    }

    protected override async Task Run(CancellationToken token)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyDbContextManager>();
        await _runner.ExecuteAsync(db, t => db.ExecAsync(ctx => ctx.GetTable<TodoItem>()
            .Where(x => x.Name == "expired")
            .DeleteAsync(t)), token: token);
    }
}
