using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Pechka.AspNet.Database;

/// <summary>
/// Drives one lazy transaction scope per registered manager on behalf of the implicit
/// entry points. Commits run sequentially in registration order; there is no cross-database
/// atomicity. Disposing without commit rolls everything back (in reverse order).
/// </summary>
internal sealed class TransactionScopeSet : IAsyncDisposable
{
    private readonly List<IDbContextTransactionScope> _scopes;
    private readonly PechkaDbTransactionOptions _options;
    private readonly ILogger _logger;
    private readonly string _operationName;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private bool _started;

    private TransactionScopeSet(List<IDbContextTransactionScope> scopes,
        PechkaDbTransactionOptions options, ILogger logger, string operationName)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
        _operationName = operationName;
    }

    public static TransactionScopeSet Begin(IReadOnlyList<ITransactionalDbContextManager> managers,
        PechkaDbTransactionOptions options, ILogger logger, string operationName)
    {
        var scopes = new List<IDbContextTransactionScope>(managers.Count);
        try
        {
            foreach (var manager in managers)
                scopes.Add(manager.BeginTransaction(options.IsolationLevel));
        }
        catch
        {
            foreach (var scope in scopes)
                scope.Dispose();
            throw;
        }
        return new TransactionScopeSet(scopes, options, logger, operationName);
    }

    public async Task CommitAsync()
    {
        _started |= _scopes.Any(s => s.IsTransactionStarted);
        foreach (var scope in _scopes)
            await scope.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _started |= _scopes.Any(s => s.IsTransactionStarted);
        for (var c = _scopes.Count - 1; c >= 0; c--)
            await _scopes[c].DisposeAsync();
        if (_started && _stopwatch.Elapsed > _options.LongTransactionWarningThreshold)
            _logger.LogWarning("Database transaction for {Operation} was held for {Elapsed}",
                _operationName, _stopwatch.Elapsed);
    }
}
