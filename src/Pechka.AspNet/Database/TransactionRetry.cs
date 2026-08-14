using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Pechka.AspNet.Database;

/// <summary>
/// Process-global sliding-window retry budget. Each retry (not first attempt) consumes one slot;
/// when the window is full, operations fail fast instead of retrying, so a systemic failure
/// isn't amplified by retry storms.
/// </summary>
internal sealed class RetryBudget
{
    private readonly PechkaDbTransactionOptions _options;
    private readonly Queue<DateTime> _retries = new();

    public RetryBudget(PechkaDbTransactionOptions options) => _options = options;

    public bool TryConsume()
    {
        var now = DateTime.UtcNow;
        lock (_retries)
        {
            while (_retries.Count > 0 && now - _retries.Peek() > _options.RetryBudgetWindow)
                _retries.Dequeue();
            if (_retries.Count >= _options.RetryBudgetMaxRetries)
                return false;
            _retries.Enqueue(now);
            return true;
        }
    }
}

internal static class TransactionRetry
{
    public static bool IsDefaultTransient(Exception? e)
    {
        for (; e != null; e = e.InnerException)
        {
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    if (IsDefaultTransient(inner))
                        return true;
                return false;
            }
            if (e is PostgresException pg && pg.SqlState is PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected)
                return true;
            if (e is NpgsqlException { IsTransient: true })
                return true;
            // 1205 = deadlock victim, -2 = timeout
            if (e is SqlException sql && (sql.Number == 1205 || sql.Number == -2))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> (called with the 1-based attempt number), retrying on
    /// transient failures per the options, subject to <paramref name="canRetry"/> and the
    /// process-global retry budget. OperationCanceledException is never retried.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(PechkaDbTransactionOptions options, ILogger logger,
        string operationName, Func<int, Task<T>> attempt, Func<Exception, bool>? canRetry = null,
        CancellationToken token = default)
    {
        var isTransient = options.IsTransientFailure ?? IsDefaultTransient;
        for (var attemptNo = 1; ; attemptNo++)
        {
            try
            {
                return await attempt(attemptNo);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (attemptNo < options.RetryMaxAttempts && isTransient(e)
                && canRetry?.Invoke(e) != false && options.Budget.TryConsume())
            {
                logger.LogWarning(e,
                    "Transient failure in {Operation} (attempt {Attempt} of {MaxAttempts}), retrying",
                    operationName, attemptNo, options.RetryMaxAttempts);
                var delay = Math.Min(options.RetryMaxDelay.TotalMilliseconds,
                    options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attemptNo - 1));
                await Task.Delay(TimeSpan.FromMilliseconds(delay * (0.5 + Random.Shared.NextDouble())), token);
            }
        }
    }
}
