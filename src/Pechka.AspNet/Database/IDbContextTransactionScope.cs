using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Pechka.AspNet.Database;

/// <summary>
/// A unit-of-work handle returned by <see cref="ITransactionalDbContextManager.BeginTransaction"/>.
/// While the scope is active, all Exec/WithTransaction calls on the owning manager share a single
/// connection and transaction, lazily opened on the first database access.
/// Disposing the scope without committing rolls the transaction back.
/// </summary>
public interface IDbContextTransactionScope : IAsyncDisposable, IDisposable
{
    /// <summary>True once the scope has been committed or rolled back.</summary>
    bool IsCompleted { get; }

    /// <summary>True if the scope was marked rollback-only (e.g. by a failed nested scope); Commit will throw.</summary>
    bool IsRollbackOnly { get; }

    /// <summary>True once a database transaction has actually been opened by the first Exec.</summary>
    bool IsTransactionStarted { get; }

    Task CommitAsync(CancellationToken token = default);

    Task RollbackAsync(CancellationToken token = default);

    /// <summary>Prevents the scope from committing; the scope owner will roll back instead.</summary>
    void SetRollbackOnly();
}

/// <summary>
/// Non-generic bridge implemented by <see cref="TransactionalDbContextManagerBase{TContext}"/> so that
/// framework plumbing (RPC interceptor, MVC filter, middleware) can drive transaction scopes without
/// knowing the concrete context type.
/// </summary>
public interface ITransactionalDbContextManager
{
    /// <summary>
    /// Enters transaction tracking mode. Nothing is opened until the first Exec call.
    /// If a scope is already active on this manager, returns a nested scope that joins it:
    /// nested Commit is a no-op vote, nested Rollback (or dispose without commit) marks the
    /// outermost scope rollback-only.
    /// </summary>
    IDbContextTransactionScope BeginTransaction(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted);

    /// <summary>The currently active scope, or null when not in tracking mode.</summary>
    IDbContextTransactionScope? CurrentTransaction { get; }
}

/// <summary>
/// Opts an RPC method/class or MVC action/controller out of the implicit per-call transaction scope;
/// its Exec calls get an independent connection each, as without transaction tracking.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NoTransactionAttribute : Attribute
{
}
