using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CoreRPC;
using CoreRPC.Transferable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pechka.AspNet.Database;

/// <summary>
/// Wraps every CoreRPC method call in transaction scopes for all registered transactional
/// managers: commit on success, rollback on exception. Opt out with [NoTransaction].
/// </summary>
internal class TransactionMethodCallInterceptor : IMethodCallInterceptor
{
    private readonly PechkaDbTransactionOptions _options;
    private readonly ILogger<TransactionMethodCallInterceptor> _logger;

    public TransactionMethodCallInterceptor(PechkaDbTransactionOptions options,
        ILogger<TransactionMethodCallInterceptor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<object> Intercept(MethodCall call, object context, Func<Task<object>> invoke)
    {
        if (!_options.InterceptRpcCalls || context is not HttpContext http || HasOptOut(call))
            return await invoke();
        var managers = http.RequestServices.GetServices<ITransactionalDbContextManager>().ToList();
        if (managers.Count == 0)
            return await invoke();

        await using var scopes = TransactionScopeSet.Begin(managers, _options, _logger,
            $"{call.Method.DeclaringType?.Name}.{call.Method.Name}");
        var rv = await invoke();
        await scopes.CommitAsync();
        return rv;
    }

    private static bool HasOptOut(MethodCall call) =>
        call.Method.GetCustomAttribute<NoTransactionAttribute>() != null
        || (call.Target?.GetType() ?? call.Method.DeclaringType)
            ?.GetCustomAttribute<NoTransactionAttribute>() != null;
}
