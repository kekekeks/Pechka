using Microsoft.Extensions.Logging.Abstractions;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests;

public class RetryCoreTests
{
    private static PechkaDbTransactionOptions Options(int maxAttempts = 3) => new()
    {
        RetryMaxAttempts = maxAttempts,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RetryMaxDelay = TimeSpan.FromMilliseconds(2),
        RetryBudgetMaxRetries = 100,
        RetryBudgetWindow = TimeSpan.FromMinutes(1),
        IsTransientFailure = e => e is FakeTransientException
    };

    private static Task<T> Execute<T>(PechkaDbTransactionOptions options, Func<int, Task<T>> attempt,
        Func<Exception, bool>? canRetry = null, CancellationToken token = default)
        => TransactionRetry.ExecuteAsync(options, NullLogger.Instance, "test", attempt, canRetry, token);

    [Fact]
    public async Task Transient_Failures_Are_Retried_Until_Success()
    {
        var attempts = new List<int>();
        var result = await Execute(Options(5), no =>
        {
            attempts.Add(no);
            if (no < 3)
                throw new FakeTransientException();
            return Task.FromResult("ok");
        });
        Assert.Equal("ok", result);
        Assert.Equal(new[] { 1, 2, 3 }, attempts);
    }

    [Fact]
    public async Task Non_Transient_Failure_Is_Rethrown_Without_Retry()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<FakePermanentException>(() => Execute<int>(Options(), _ =>
        {
            attempts++;
            throw new FakePermanentException();
        }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Attempts_Are_Capped_At_RetryMaxAttempts()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<FakeTransientException>(() => Execute<int>(Options(4), _ =>
        {
            attempts++;
            throw new FakeTransientException();
        }));
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task OperationCanceledException_Is_Never_Retried()
    {
        var options = Options(5);
        // Even a classifier that calls everything transient must not defeat the cancellation guard
        options.IsTransientFailure = _ => true;
        var attempts = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Execute<int>(options, _ =>
        {
            attempts++;
            throw new OperationCanceledException();
        }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CanRetry_False_Suppresses_Retries()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<FakeTransientException>(() => Execute<int>(Options(5), _ =>
        {
            attempts++;
            throw new FakeTransientException();
        }, canRetry: _ => false));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CanRetry_Sees_The_Thrown_Exception()
    {
        var seen = new List<Exception>();
        await Assert.ThrowsAsync<FakeTransientException>(() => Execute<int>(Options(2), _ =>
            throw new FakeTransientException("boom"), canRetry: e =>
        {
            seen.Add(e);
            return true;
        }));
        Assert.Equal("boom", Assert.Single(seen).Message);
    }

    [Fact]
    public async Task Without_A_Custom_Classifier_Plain_Exceptions_Are_Not_Retried()
    {
        var options = Options(5);
        options.IsTransientFailure = null;
        var attempts = 0;
        await Assert.ThrowsAsync<FakeTransientException>(() => Execute<int>(options, _ =>
        {
            attempts++;
            throw new FakeTransientException();
        }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_Successful_First_Attempt_Runs_Exactly_Once()
    {
        var attempts = 0;
        var result = await Execute(Options(), no =>
        {
            attempts++;
            return Task.FromResult(no);
        });
        Assert.Equal(1, result);
        Assert.Equal(1, attempts);
    }
}
