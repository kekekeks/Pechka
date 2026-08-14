using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests;

public class TransactionScopeTests : SqliteTestBase
{
    public TransactionScopeTests(SqliteTestDatabase db) : base(db)
    {
    }

    [Fact]
    public async Task Exec_Without_Scope_Autocommits()
    {
        await using var manager = Db.CreateManager();
        Assert.Null(manager.CurrentTransaction);
        await Insert(manager, "a");
        await Insert(manager, "b");
        Assert.Equal(new[] { "a", "b" }, await ReadItemNames());
    }

    [Fact]
    public async Task BeginTransaction_Is_Lazy()
    {
        await using var manager = Db.CreateManager();
        using var scope = manager.BeginTransaction();
        Assert.False(scope.IsTransactionStarted);
        Assert.False(scope.IsCompleted);
        Assert.Same(scope, manager.CurrentTransaction);
    }

    [Fact]
    public async Task First_Exec_Starts_The_Transaction()
    {
        await using var manager = Db.CreateManager();
        using var scope = manager.BeginTransaction();
        await Insert(manager, "a");
        Assert.True(scope.IsTransactionStarted);
        await scope.CommitAsync();
    }

    [Fact]
    public async Task Untouched_Scope_Commits_As_NoOp()
    {
        await using var manager = Db.CreateManager();
        var scope = manager.BeginTransaction();
        await scope.CommitAsync();
        Assert.True(scope.IsCompleted);
        Assert.False(scope.IsTransactionStarted);
        scope.Dispose();
    }

    [Fact]
    public async Task Untouched_Scope_Disposes_As_NoOp()
    {
        await using var manager = Db.CreateManager();
        var scope = manager.BeginTransaction();
        await scope.DisposeAsync();
        Assert.True(scope.IsCompleted);
        Assert.Null(manager.CurrentTransaction);
    }

    [Fact]
    public async Task Commit_Persists()
    {
        await using var manager = Db.CreateManager();
        using (var scope = manager.BeginTransaction())
        {
            await Insert(manager, "a");
            Assert.Empty(await ReadItemNames());
            await scope.CommitAsync();
        }
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task Rollback_Discards()
    {
        await using var manager = Db.CreateManager();
        using var scope = manager.BeginTransaction();
        await Insert(manager, "a");
        await scope.RollbackAsync();
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task Dispose_Without_Commit_Rolls_Back()
    {
        await using var manager = Db.CreateManager();
        var scope = manager.BeginTransaction();
        await Insert(manager, "a");
        await scope.DisposeAsync();
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task Double_Complete_Throws()
    {
        await using var manager = Db.CreateManager();
        var scope = manager.BeginTransaction();
        await Insert(manager, "a");
        await scope.CommitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.RollbackAsync());
    }

    [Fact]
    public async Task Commit_Of_RollbackOnly_Scope_Rolls_Back_And_Throws()
    {
        await using var manager = Db.CreateManager();
        var scope = manager.BeginTransaction();
        await Insert(manager, "a");
        scope.SetRollbackOnly();
        Assert.True(scope.IsRollbackOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CommitAsync());
        Assert.True(scope.IsCompleted);
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task After_Completion_Manager_Falls_Back_To_Fresh_Connections()
    {
        await using var manager = Db.CreateManager();
        var scope = manager.BeginTransaction();
        await Insert(manager, "a");
        await scope.CommitAsync();
        Assert.Null(manager.CurrentTransaction);
        await Insert(manager, "b");
        Assert.Equal(new[] { "a", "b" }, await ReadItemNames());
    }

    [Fact]
    public async Task Nested_Scope_Joins_Root_And_Its_Commit_Is_Only_A_Vote()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        var nested = manager.BeginTransaction();
        Assert.NotSame(root, nested);
        Assert.Same(root, manager.CurrentTransaction);

        await Insert(manager, "a");
        Assert.True(nested.IsTransactionStarted);
        await nested.CommitAsync();
        Assert.True(nested.IsCompleted);
        // The vote must not have touched the database transaction
        Assert.Empty(await ReadItemNames());
        Assert.Same(root, manager.CurrentTransaction);

        await root.CommitAsync();
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task Nested_Rollback_Poisons_The_Root()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        var nested = manager.BeginTransaction();
        await Insert(manager, "a");
        await nested.RollbackAsync();
        Assert.True(root.IsRollbackOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => root.CommitAsync());
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task Nested_Dispose_Without_Commit_Poisons_The_Root()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        using (manager.BeginTransaction())
            await Insert(manager, "a");
        Assert.True(root.IsRollbackOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => root.CommitAsync());
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task CurrentTransaction_Is_Null_Once_The_Root_Completes()
    {
        await using var manager = Db.CreateManager();
        Assert.Null(manager.CurrentTransaction);
        var root = manager.BeginTransaction();
        Assert.Same(root, manager.CurrentTransaction);
        await root.CommitAsync();
        Assert.Null(manager.CurrentTransaction);
    }

    [Fact]
    public async Task WithTransaction_Joins_An_Active_Scope()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        var result = await manager.WithTransaction(async ctx =>
        {
            await ctx.InsertAsync(new TestItem { Name = "a" });
            return 42;
        });
        Assert.Equal(42, result);
        // WithTransaction did not commit on its own
        Assert.Empty(await ReadItemNames());
        await root.CommitAsync();
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task The_Non_Generic_WithTransaction_Also_Joins_An_Active_Scope()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        await manager.WithTransaction(ctx => ctx.InsertAsync(new TestItem { Name = "a" }));
        Assert.Empty(await ReadItemNames());
        await root.CommitAsync();
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task WithTransaction_Failure_Poisons_The_Active_Scope()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        await Insert(manager, "a");
        await Assert.ThrowsAsync<FakePermanentException>(() => manager.WithTransaction<int>(
            _ => throw new FakePermanentException()));
        Assert.True(root.IsRollbackOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => root.CommitAsync());
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task WithTransaction_Without_A_Scope_Commits_On_Success()
    {
        await using var manager = Db.CreateManager();
        await manager.WithTransaction(async ctx =>
        {
            await ctx.InsertAsync(new TestItem { Name = "a" });
            return 0;
        });
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task WithTransaction_Without_A_Scope_Rolls_Back_On_Failure()
    {
        await using var manager = Db.CreateManager();
        await Assert.ThrowsAsync<FakePermanentException>(() => manager.WithTransaction<int>(async ctx =>
        {
            await ctx.InsertAsync(new TestItem { Name = "a" });
            throw new FakePermanentException();
        }));
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task Concurrent_Execs_In_One_Scope_Are_Serialized_And_Atomic()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        var names = Enumerable.Range(0, 8).Select(i => $"c{i}").ToArray();
        await Task.WhenAll(names.Select(n => Insert(manager, n)));
        Assert.Empty(await ReadItemNames());
        await root.CommitAsync();
        Assert.Equal(names.OrderBy(x => x), (await ReadItemNames()).OrderBy(x => x));
    }

    [Fact]
    public async Task Reentrant_Exec_Inside_An_Exec_Callback_Reuses_The_Context()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        TestDbContext? outerCtx = null;
        TestDbContext? innerCtx = null;
        await manager.ExecAsync(async ctx =>
        {
            outerCtx = ctx;
            await ctx.InsertAsync(new TestItem { Name = "outer" });
            await manager.ExecAsync(async inner =>
            {
                innerCtx = inner;
                await inner.InsertAsync(new TestItem { Name = "inner" });
            });
        });
        Assert.Same(outerCtx, innerCtx);
        await root.CommitAsync();
        Assert.Equal(new[] { "outer", "inner" }, await ReadItemNames());
    }

    [Fact]
    public async Task Commit_From_Inside_An_Exec_Callback_Works()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        await manager.ExecAsync(async ctx =>
        {
            await ctx.InsertAsync(new TestItem { Name = "a" });
            await root.CommitAsync();
        });
        Assert.Equal(new[] { "a" }, await ReadItemNames());
        Assert.Null(manager.CurrentTransaction);
    }

    [Fact]
    public async Task OnCommitted_Fires_After_A_Successful_Commit()
    {
        await using var manager = Db.CreateManager();
        var fired = 0;
        var root = manager.BeginTransaction();
        root.OnCommitted(() => fired++);
        await Insert(manager, "a");
        Assert.Equal(0, fired);
        await root.CommitAsync();
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task OnCommitted_Fires_For_A_Scope_That_Never_Started_A_Transaction()
    {
        await using var manager = Db.CreateManager();
        var fired = false;
        var root = manager.BeginTransaction();
        root.OnCommitted(() => fired = true);
        await root.CommitAsync();
        Assert.True(fired);
        Assert.False(root.IsTransactionStarted);
    }

    [Fact]
    public async Task OnCommitted_Is_Dropped_On_Rollback_And_On_Dispose()
    {
        await using var manager = Db.CreateManager();
        var fired = false;

        var rolledBack = manager.BeginTransaction();
        rolledBack.OnCommitted(() => fired = true);
        await rolledBack.RollbackAsync();
        Assert.False(fired);

        var disposed = manager.BeginTransaction();
        disposed.OnCommitted(() => fired = true);
        await disposed.DisposeAsync();
        Assert.False(fired);
    }

    [Fact]
    public async Task Nested_OnCommitted_Fires_With_The_Root_Commit()
    {
        await using var manager = Db.CreateManager();
        var fired = 0;
        var root = manager.BeginTransaction();
        var nested = manager.BeginTransaction();
        nested.OnCommitted(() => fired++);
        await nested.CommitAsync();
        Assert.Equal(0, fired);
        await root.CommitAsync();
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task OnCommitted_On_A_Completed_Scope_Throws()
    {
        await using var manager = Db.CreateManager();
        var root = manager.BeginTransaction();
        await root.CommitAsync();
        Assert.Throws<ObjectDisposedException>(() => root.OnCommitted(() => { }));
    }

    [Fact]
    public async Task Exec_Returning_IQueryable_Throws_Without_A_Scope()
    {
        await using var manager = Db.CreateManager();
        Assert.Throws<InvalidOperationException>(
            () => manager.Exec(ctx => ctx.GetTable<TestItem>().Where(x => x.Id > 0)));
    }

    [Fact]
    public async Task Exec_Returning_IQueryable_Throws_Inside_A_Scope()
    {
        await using var manager = Db.CreateManager();
        using var root = manager.BeginTransaction();
        Assert.Throws<InvalidOperationException>(
            () => manager.Exec(ctx => ctx.GetTable<TestItem>().Where(x => x.Id > 0)));
    }

    [Fact]
    public async Task Manager_DisposeAsync_Rolls_Back_A_Dangling_Scope()
    {
        var manager = Db.CreateManager();
        var scope = manager.BeginTransaction();
        await Insert(manager, "a");
        await manager.DisposeAsync();
        Assert.True(scope.IsCompleted);
        Assert.Empty(await ReadItemNames());
    }
}
