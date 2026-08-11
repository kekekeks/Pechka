using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Pechka.AspNet.BackgroundServices;

namespace MyWebApp;

// Ticking services stay singletons; a scoped transactional manager is obtained
// by creating a DI scope per tick, with an explicit transaction scope around the work.
public class ExpiredTodoCleanupService : TickingServiceBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ExpiredTodoCleanupService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        Interval = TimeSpan.FromSeconds(5);
    }

    protected override async Task Run(CancellationToken token)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyDbContextManager>();
        await using var tx = db.BeginTransaction();
        await db.ExecAsync(ctx => ctx.GetTable<TodoItem>()
            .Where(x => x.Name == "expired")
            .DeleteAsync(token));
        await tx.CommitAsync(token);
    }
}
