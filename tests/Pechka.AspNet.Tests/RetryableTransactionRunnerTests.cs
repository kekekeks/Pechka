using Microsoft.Extensions.Logging.Abstractions;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests;

public class RetryableTransactionRunnerTests : SqliteTestBase
{
    public RetryableTransactionRunnerTests(SqliteTestDatabase db) : base(db)
    {
    }

    private static PechkaDbTransactionOptions Options(int maxAttempts = 3, int budget = 100) => new()
    {
        RetryMaxAttempts = maxAttempts,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RetryMaxDelay = TimeSpan.FromMilliseconds(2),
        RetryBudgetMaxRetries = budget,
        RetryBudgetWindow = TimeSpan.FromMinutes(1),
        IsTransientFailure = e => e is FakeTransientException
    };

    private static RetryableTransactionRunner Runner(PechkaDbTransactionOptions options) =>
        new(options, NullLogger<RetryableTransactionRunner>.Instance);

    [Fact]
    public async Task A_Failed_Attempt_Is_Rolled_Back_And_The_Retry_Commits_Once()
    {
        await using var manager = Db.CreateManager();
        var attempts = 0;
        await Runner(Options()).ExecuteAsync(manager, async _ =>
        {
            attempts++;
            await Insert(manager, "a");
            if (attempts == 1)
                throw new FakeTransientException();
        });
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task A_Non_Transient_Failure_Propagates_And_Rolls_Back()
    {
        await using var manager = Db.CreateManager();
        var attempts = 0;
        await Assert.ThrowsAsync<FakePermanentException>(() => Runner(Options()).ExecuteAsync(manager,
            async _ =>
            {
                attempts++;
                await Insert(manager, "a");
                throw new FakePermanentException();
            }));
        Assert.Equal(1, attempts);
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task Attempts_Are_Capped_And_Nothing_Is_Persisted()
    {
        await using var manager = Db.CreateManager();
        var attempts = 0;
        await Assert.ThrowsAsync<FakeTransientException>(() => Runner(Options(maxAttempts: 4))
            .ExecuteAsync(manager, async _ =>
            {
                attempts++;
                await Insert(manager, "a");
                throw new FakeTransientException();
            }));
        Assert.Equal(4, attempts);
        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task An_Exhausted_Budget_Prevents_Retries()
    {
        await using var manager = Db.CreateManager();
        var attempts = 0;
        await Assert.ThrowsAsync<FakeTransientException>(() => Runner(Options(budget: 0))
            .ExecuteAsync(manager, _ =>
            {
                attempts++;
                throw new FakeTransientException();
            }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task The_Generic_Overload_Returns_The_Last_Attempts_Result()
    {
        await using var manager = Db.CreateManager();
        var attempts = 0;
        var result = await Runner(Options()).ExecuteAsync(manager, async _ =>
        {
            attempts++;
            await Insert(manager, $"a{attempts}");
            if (attempts == 1)
                throw new FakeTransientException();
            return attempts;
        });
        Assert.Equal(2, result);
        Assert.Equal(new[] { "a2" }, await ReadItemNames());
    }

    [Fact]
    public async Task Each_Attempt_Gets_A_Fresh_Scope()
    {
        await using var manager = Db.CreateManager();
        var scopes = new List<IDbContextTransactionScope>();
        var attempts = 0;
        await Runner(Options()).ExecuteAsync(manager, async _ =>
        {
            attempts++;
            scopes.Add(manager.CurrentTransaction!);
            await Insert(manager, "a");
            if (attempts == 1)
                throw new FakeTransientException();
        });
        Assert.Equal(2, scopes.Count);
        Assert.NotSame(scopes[0], scopes[1]);
        Assert.True(scopes[0].IsCompleted);
    }
}
