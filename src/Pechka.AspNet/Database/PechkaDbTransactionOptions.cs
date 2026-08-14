using System;
using System.Data;
using System.Threading;

namespace Pechka.AspNet.Database;

/// <summary>
/// Settings for the implicit unit-of-work entry points created by AddTransactionalDbContextManager.
/// Shared by all registered transactional managers.
/// </summary>
public class PechkaDbTransactionOptions
{
    /// <summary>Wrap every CoreRPC method call (unless marked with [NoTransaction]) in a transaction scope.</summary>
    public bool InterceptRpcCalls { get; set; } = true;

    /// <summary>Wrap every MVC action (unless marked with [NoTransaction]) in a transaction scope.</summary>
    public bool InterceptMvcActions { get; set; } = true;

    /// <summary>Isolation level used by the implicit entry points.</summary>
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.ReadCommitted;

    /// <summary>
    /// A warning is logged when an implicitly started transaction is held longer than this
    /// (typically a slow external call inside a transactional handler).
    /// </summary>
    public TimeSpan LongTransactionWarningThreshold { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Re-run RPC calls / MVC actions whose unit of work failed with a transient database error
    /// (serialization failure, deadlock, dropped connection). Safe when handlers keep their side
    /// effects inside the unit of work (e.g. as background jobs); opt individual endpoints out
    /// with [NoRetry]. MVC additionally needs UsePechkaTransactionRetries() after UseRouting.
    /// </summary>
    public bool EnableRetries { get; set; }

    /// <summary>Total attempts per operation, including the first one.</summary>
    public int RetryMaxAttempts { get; set; } = 3;

    /// <summary>Base delay before a retry; grows exponentially per attempt, with jitter.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Upper bound for the backoff delay.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Custom transient-failure classifier; null uses the built-in one
    /// (Postgres 40001/40P01, transient Npgsql errors, SqlServer deadlock/timeout).</summary>
    public Func<Exception, bool>? IsTransientFailure { get; set; }

    /// <summary>
    /// Process-global retry budget: at most this many retries within <see cref="RetryBudgetWindow"/>
    /// across all operations. Once exhausted, failures propagate without retrying, so a systemic
    /// outage isn't amplified by retry storms.
    /// </summary>
    public int RetryBudgetMaxRetries { get; set; } = 20;

    /// <summary>Sliding window for <see cref="RetryBudgetMaxRetries"/>.</summary>
    public TimeSpan RetryBudgetWindow { get; set; } = TimeSpan.FromSeconds(10);

    private RetryBudget? _budget;

    // The options object is a process singleton, which makes the budget process-global
    internal RetryBudget Budget => LazyInitializer.EnsureInitialized(ref _budget, () => new RetryBudget(this));
}
