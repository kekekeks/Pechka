using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinqToDB.Data;

namespace Pechka.AspNet.Database;

/// <summary>
/// A <see cref="DbContextManagerBase{TContext}"/> variant with unit-of-work transaction tracking.
/// Register it as a scoped service via AddTransactionalDbContextManager. While a scope returned by
/// <see cref="BeginTransaction"/> is active, all Exec/WithTransaction calls share one connection and
/// transaction (lazily opened on first use) and are serialized; without an active scope the manager
/// behaves exactly like the base class. Work forked with Task.Run from inside an Exec callback is
/// not supported while a scope is active.
/// </summary>
public class TransactionalDbContextManagerBase<TContext> : DbContextManagerBase<TContext>,
    ITransactionalDbContextManager, IAsyncDisposable
    where TContext : DataConnection
{
    private readonly Func<TContext> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _reentrant = new();
    private RootScope? _root;

    protected TransactionalDbContextManagerBase(Func<TContext> factory) : base(factory)
        => _factory = factory;

    public IDbContextTransactionScope? CurrentTransaction => ActiveRoot;

    private RootScope? ActiveRoot
    {
        get
        {
            var root = _root;
            return root == null || root.IsCompleted ? null : root;
        }
    }

    public IDbContextTransactionScope BeginTransaction(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
    {
        var active = ActiveRoot;
        if (active != null)
            return new NestedScope(active);
        var root = new RootScope(this, isolationLevel);
        _root = root;
        return root;
    }

    public override void Exec(Action<TContext> cb)
    {
        var root = ActiveRoot;
        if (root == null)
            base.Exec(cb);
        else
            root.Run(ctx =>
            {
                cb(ctx);
                return true;
            });
    }

    public override T Exec<T>(Func<TContext, T> cb)
    {
        var root = ActiveRoot;
        if (root == null)
            return base.Exec(cb);
        var rv = root.Run(cb);
        if (rv is IQueryable)
            throw new InvalidOperationException("IQueryable leak detected");
        return rv;
    }

    public override Task<T> ExecAsync<T>(Func<TContext, Task<T>> cb)
    {
        var root = ActiveRoot;
        return root == null ? base.ExecAsync(cb) : root.RunAsync(cb);
    }

    public override Task ExecAsync(Func<TContext, Task> cb)
    {
        var root = ActiveRoot;
        if (root == null)
            return base.ExecAsync(cb);
        return root.RunAsync(async ctx =>
        {
            await cb(ctx);
            return true;
        });
    }

    public override async Task<T> WithTransaction<T>(
        Func<TContext, Task<T>> action,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken token = default)
    {
        var root = ActiveRoot;
        if (root == null)
            return await base.WithTransaction(action, isolationLevel, token);
        // Joins the active scope: commit is deferred to the scope owner, failure poisons it.
        // The requested isolation level is only honored if the transaction hasn't started yet.
        root.TryUpgradeIsolation(isolationLevel);
        try
        {
            return await root.RunAsync(action, token);
        }
        catch
        {
            root.SetRollbackOnly();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var root = _root;
        if (root != null)
            await root.DisposeAsync();
        _gate.Dispose();
    }

    private sealed class RootScope : IDbContextTransactionScope
    {
        private const int StateOpen = 0;
        private const int StateCommitted = 1;
        private const int StateRolledBack = 2;

        private readonly TransactionalDbContextManagerBase<TContext> _owner;
        private TContext? _ctx;
        private DataConnectionTransaction? _tx;
        private IsolationLevel _isolationLevel;
        private int _state;
        private volatile bool _rollbackOnly;

        public RootScope(TransactionalDbContextManagerBase<TContext> owner, IsolationLevel isolationLevel)
        {
            _owner = owner;
            _isolationLevel = isolationLevel;
        }

        public bool IsCompleted => _state != StateOpen;
        public bool IsRollbackOnly => _rollbackOnly;
        public bool IsTransactionStarted => _tx != null;

        public void SetRollbackOnly() => _rollbackOnly = true;

        public void TryUpgradeIsolation(IsolationLevel isolationLevel)
        {
            if (_tx == null && isolationLevel > _isolationLevel)
                _isolationLevel = isolationLevel;
        }

        public T Run<T>(Func<TContext, T> cb)
        {
            // A callback inside an Exec calling Exec again on the same async flow already
            // holds the gate; taking it again would deadlock.
            if (_owner._reentrant.Value)
                return cb(GetOrCreateContext());
            _owner._gate.Wait();
            try
            {
                _owner._reentrant.Value = true;
                try
                {
                    return cb(GetOrCreateContext());
                }
                finally
                {
                    _owner._reentrant.Value = false;
                }
            }
            finally
            {
                _owner._gate.Release();
            }
        }

        public async Task<T> RunAsync<T>(Func<TContext, Task<T>> cb, CancellationToken token = default)
        {
            if (_owner._reentrant.Value)
                return await cb(await GetOrCreateContextAsync(token));
            await _owner._gate.WaitAsync(token);
            try
            {
                _owner._reentrant.Value = true;
                try
                {
                    return await cb(await GetOrCreateContextAsync(token));
                }
                finally
                {
                    _owner._reentrant.Value = false;
                }
            }
            finally
            {
                _owner._gate.Release();
            }
        }

        private TContext GetOrCreateContext()
        {
            ThrowIfCompleted();
            if (_ctx == null)
            {
                var ctx = _owner._factory();
                try
                {
                    _tx = ctx.BeginTransaction(_isolationLevel);
                }
                catch
                {
                    ctx.Dispose();
                    throw;
                }
                _ctx = ctx;
            }
            return _ctx;
        }

        private async ValueTask<TContext> GetOrCreateContextAsync(CancellationToken token)
        {
            ThrowIfCompleted();
            if (_ctx == null)
            {
                var ctx = _owner._factory();
                try
                {
                    _tx = await ctx.BeginTransactionAsync(_isolationLevel, token);
                }
                catch
                {
                    await ctx.DisposeAsync();
                    throw;
                }
                _ctx = ctx;
            }
            return _ctx;
        }

        private void ThrowIfCompleted()
        {
            if (IsCompleted)
                throw new ObjectDisposedException(nameof(IDbContextTransactionScope),
                    "The transaction scope is already completed");
        }

        public async Task CommitAsync(CancellationToken token = default)
        {
            if (_rollbackOnly)
            {
                if (Interlocked.CompareExchange(ref _state, StateRolledBack, StateOpen) == StateOpen)
                    await FinishAsync(commit: false, token);
                throw new InvalidOperationException("Transaction scope was marked as rollback-only");
            }
            if (Interlocked.CompareExchange(ref _state, StateCommitted, StateOpen) != StateOpen)
                throw new InvalidOperationException("Transaction scope is already completed");
            await FinishAsync(commit: true, token);
        }

        public async Task RollbackAsync(CancellationToken token = default)
        {
            if (Interlocked.CompareExchange(ref _state, StateRolledBack, StateOpen) != StateOpen)
                throw new InvalidOperationException("Transaction scope is already completed");
            await FinishAsync(commit: false, token);
        }

        private async Task FinishAsync(bool commit, CancellationToken token)
        {
            // Commit from inside an Exec callback already holds the gate on this flow.
            var ownsGate = !_owner._reentrant.Value;
            if (ownsGate)
                await _owner._gate.WaitAsync(token);
            try
            {
                if (_tx != null)
                {
                    if (commit)
                        await _tx.CommitAsync(token);
                    else
                        await _tx.RollbackAsync(token);
                }
            }
            finally
            {
                var ctx = _ctx;
                _tx = null;
                _ctx = null;
                if (ctx != null)
                    await ctx.DisposeAsync();
                if (ownsGate)
                    _owner._gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _state, StateRolledBack, StateOpen) != StateOpen)
                return;
            var ownsGate = !_owner._reentrant.Value;
            if (ownsGate)
                await _owner._gate.WaitAsync();
            try
            {
                var ctx = _ctx;
                _tx = null;
                _ctx = null;
                // DataConnection dispose rolls back the still-open transaction
                if (ctx != null)
                    await ctx.DisposeAsync();
            }
            finally
            {
                if (ownsGate)
                    _owner._gate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _state, StateRolledBack, StateOpen) != StateOpen)
                return;
            var ownsGate = !_owner._reentrant.Value;
            if (ownsGate)
                _owner._gate.Wait();
            try
            {
                var ctx = _ctx;
                _tx = null;
                _ctx = null;
                ctx?.Dispose();
            }
            finally
            {
                if (ownsGate)
                    _owner._gate.Release();
            }
        }
    }

    private sealed class NestedScope : IDbContextTransactionScope
    {
        private const int StateOpen = 0;
        private const int StateCommitted = 1;
        private const int StateRolledBack = 2;

        private readonly RootScope _root;
        private int _state;

        public NestedScope(RootScope root) => _root = root;

        public bool IsCompleted => _state != StateOpen;
        public bool IsRollbackOnly => _root.IsRollbackOnly;
        public bool IsTransactionStarted => _root.IsTransactionStarted;

        public void SetRollbackOnly() => _root.SetRollbackOnly();

        public Task CommitAsync(CancellationToken token = default)
        {
            if (Interlocked.CompareExchange(ref _state, StateCommitted, StateOpen) != StateOpen)
                throw new InvalidOperationException("Transaction scope is already completed");
            if (_root.IsRollbackOnly)
                throw new InvalidOperationException("Transaction scope was marked as rollback-only");
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken token = default)
        {
            if (Interlocked.CompareExchange(ref _state, StateRolledBack, StateOpen) != StateOpen)
                throw new InvalidOperationException("Transaction scope is already completed");
            _root.SetRollbackOnly();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            // Dispose without commit is a rollback vote
            if (Interlocked.CompareExchange(ref _state, StateRolledBack, StateOpen) == StateOpen)
                _root.SetRollbackOnly();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
