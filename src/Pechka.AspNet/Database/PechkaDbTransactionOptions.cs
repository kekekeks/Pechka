using System;
using System.Data;

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
}
