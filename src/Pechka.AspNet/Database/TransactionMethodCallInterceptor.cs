using System;
using System.Collections.Generic;
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
/// With EnableRetries, transient failures re-run the whole call ([NoRetry] to opt out);
/// note that the RPC target instance is reused across attempts.
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
        if (!_options.InterceptRpcCalls || context is not HttpContext http || HasAttribute<NoTransactionAttribute>(call))
            return await invoke();
        var managers = http.RequestServices.GetServices<ITransactionalDbContextManager>().ToList();
        if (managers.Count == 0)
            return await invoke();

        var operationName = $"{call.Method.DeclaringType?.Name}.{call.Method.Name}";
        if (!_options.EnableRetries || HasAttribute<NoRetryAttribute>(call))
            return await RunAttempt(managers, operationName, invoke);
        // The invoke delegate re-runs the handler on the same target instance with the same
        // deserialized arguments, so a rolled-back attempt can be repeated wholesale
        return await TransactionRetry.ExecuteAsync(_options, _logger, operationName,
            _ => RunAttempt(managers, operationName, invoke),
            token: http.RequestAborted);
    }

    private async Task<object> RunAttempt(List<ITransactionalDbContextManager> managers,
        string operationName, Func<Task<object>> invoke)
    {
        await using var scopes = TransactionScopeSet.Begin(managers, _options, _logger, operationName);
        var rv = await invoke();
        await scopes.CommitAsync();
        return rv;
    }

    private static bool HasAttribute<TAttribute>(MethodCall call) where TAttribute : Attribute =>
        call.Method.GetCustomAttribute<TAttribute>() != null
        || (call.Target?.GetType() ?? call.Method.DeclaringType)
            ?.GetCustomAttribute<TAttribute>() != null;
}
