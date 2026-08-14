using Microsoft.Extensions.Logging.Abstractions;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests;

public class RetryBudgetTests
{
    private static PechkaDbTransactionOptions Options(int budget, TimeSpan? window = null) => new()
    {
        RetryMaxAttempts = 10,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RetryMaxDelay = TimeSpan.FromMilliseconds(2),
        RetryBudgetMaxRetries = budget,
        RetryBudgetWindow = window ?? TimeSpan.FromMinutes(1),
        IsTransientFailure = e => e is FakeTransientException
    };

    [Fact]
    public void TryConsume_Allows_The_Configured_Number_Of_Retries_Then_Refuses()
    {
        var budget = new RetryBudget(Options(3));
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.False(budget.TryConsume());
    }

    [Fact]
    public void A_Zero_Budget_Refuses_Immediately()
    {
        Assert.False(new RetryBudget(Options(0)).TryConsume());
    }

    [Fact]
    public async Task Budget_Refills_After_The_Window_Elapses()
    {
        var window = TimeSpan.FromMilliseconds(100);
        var budget = new RetryBudget(Options(1, window));
        Assert.True(budget.TryConsume());
        Assert.False(budget.TryConsume());
        await Task.Delay(window + TimeSpan.FromMilliseconds(100));
        Assert.True(budget.TryConsume());
    }

    [Fact]
    public async Task Budget_Is_Shared_Across_Sequential_ExecuteAsync_Calls()
    {
        var options = Options(1);
        var first = 0;
        var second = 0;
        await Assert.ThrowsAsync<FakeTransientException>(() => Run(options, () => first++));
        await Assert.ThrowsAsync<FakeTransientException>(() => Run(options, () => second++));
        // The single budgeted retry went to the first operation
        Assert.Equal(2, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task Budget_Is_Shared_Across_Concurrent_ExecuteAsync_Calls()
    {
        var options = Options(1);
        var attempts = 0;
        var tasks = Enumerable.Range(0, 2)
            .Select(_ => Assert.ThrowsAsync<FakeTransientException>(
                () => Run(options, () => Interlocked.Increment(ref attempts))))
            .ToArray();
        await Task.WhenAll(tasks);
        // Two first attempts plus exactly one budgeted retry
        Assert.Equal(3, attempts);
    }

    private static Task Run(PechkaDbTransactionOptions options, Action onAttempt)
        => TransactionRetry.ExecuteAsync<int>(options, NullLogger.Instance, "test", _ =>
        {
            onAttempt();
            throw new FakeTransientException();
        });
}
