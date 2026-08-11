using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Pechka.AspNet.Database;

/// <summary>
/// Wraps every MVC action in transaction scopes for all registered transactional managers:
/// commit on success, rollback on unhandled exception. Opt out with [NoTransaction].
/// Instantiated per request from the request scope.
/// </summary>
internal class TransactionActionFilter : IAsyncActionFilter
{
    private readonly List<ITransactionalDbContextManager> _managers;
    private readonly PechkaDbTransactionOptions _options;
    private readonly ILogger<TransactionActionFilter> _logger;

    public TransactionActionFilter(IEnumerable<ITransactionalDbContextManager> managers,
        PechkaDbTransactionOptions options, ILogger<TransactionActionFilter> logger)
    {
        _managers = managers.ToList();
        _options = options;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!_options.InterceptMvcActions || _managers.Count == 0 || HasOptOut(context))
        {
            await next();
            return;
        }

        await using var scopes = TransactionScopeSet.Begin(_managers, _options, _logger,
            context.ActionDescriptor.DisplayName ?? "MVC action");
        var executed = await next();
        if (executed.Exception == null || executed.ExceptionHandled)
            await scopes.CommitAsync();
    }

    private static bool HasOptOut(ActionExecutingContext context) =>
        context.ActionDescriptor is ControllerActionDescriptor cad
        && (cad.MethodInfo.GetCustomAttribute<NoTransactionAttribute>() != null
            || cad.ControllerTypeInfo.GetCustomAttribute<NoTransactionAttribute>() != null);
}
