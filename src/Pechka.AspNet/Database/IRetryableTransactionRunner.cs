using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Pechka.AspNet.Database;

/// <summary>
/// Runs an action inside a transaction scope on the given manager, retrying transient database
/// failures (each attempt gets a fresh scope; commit included). This is the manual counterpart of
/// the implicit request retries for ticking services and other custom code — calling it is the
/// opt-in, regardless of PechkaDbTransactionOptions.EnableRetries; the attempt limits, backoff and
/// the process-global retry budget still come from the options. The action must keep its side
/// effects inside the unit of work. Do not call with an ambient scope already active on the
/// manager: a failed attempt would poison it, making retries futile.
/// </summary>
public interface IRetryableTransactionRunner
{
    Task<T> ExecuteAsync<T>(ITransactionalDbContextManager manager, Func<CancellationToken, Task<T>> action,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken token = default);

    Task ExecuteAsync(ITransactionalDbContextManager manager, Func<CancellationToken, Task> action,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken token = default);
}

internal sealed class RetryableTransactionRunner : IRetryableTransactionRunner
{
    private readonly PechkaDbTransactionOptions _options;
    private readonly ILogger<RetryableTransactionRunner> _logger;

    public RetryableTransactionRunner(PechkaDbTransactionOptions options,
        ILogger<RetryableTransactionRunner> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<T> ExecuteAsync<T>(ITransactionalDbContextManager manager,
        Func<CancellationToken, Task<T>> action, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken token = default)
        => TransactionRetry.ExecuteAsync(_options, _logger, "retryable transaction", async _ =>
        {
            await using var tx = manager.BeginTransaction(isolationLevel);
            var result = await action(token);
            await tx.CommitAsync(token);
            return result;
        }, token: token);

    public Task ExecuteAsync(ITransactionalDbContextManager manager, Func<CancellationToken, Task> action,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken token = default)
        => ExecuteAsync<object?>(manager, async t =>
        {
            await action(t);
            return null;
        }, isolationLevel, token);
}
