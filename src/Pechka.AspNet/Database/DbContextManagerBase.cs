using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinqToDB.Data;

namespace Pechka.AspNet.Database
{
    public class DbContextManagerBase<TContext> where TContext : DataConnection
    {
        private readonly Func<TContext> _factory;

        protected DbContextManagerBase(Func<TContext> factory) => _factory = factory;

        public void Exec(Action<TContext> cb)
        {
            using var ctx = _factory();
            cb(ctx);
        }

        public T Exec<T>(Func<TContext, T> cb)
        {
            using var ctx = _factory();
            var rv = cb(ctx);
            if (rv is IQueryable)
                throw new InvalidOperationException("IQueryable leak detected");
            return rv;
        }

        public async Task<T> ExecAsync<T>(Func<TContext, Task<T>> cb)
        {
            await using var ctx = _factory();
            return await cb(ctx);
        }

        public async Task ExecAsync(Func<TContext, Task> cb)
        {
            await using var ctx = _factory();
            await cb(ctx);
        }

        public async Task WithTransaction(
            Func<TContext, Task> action,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken token = default)
        {
            await WithTransaction(async db =>
            {
                await action(db);
                return true;
            }, isolationLevel, token);
        }

        public async Task<T> WithTransaction<T>(
            Func<TContext, Task<T>> action,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken token = default)
        {
            using var context = _factory();
            using var transaction = await context
                .BeginTransactionAsync(isolationLevel, token);
            try
            {
                var result = await action(context);
                await transaction.CommitAsync(token);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }
    }
}