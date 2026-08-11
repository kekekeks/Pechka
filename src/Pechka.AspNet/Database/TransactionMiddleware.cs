using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pechka.AspNet.Database;

/// <summary>
/// Optional per-request unit of work for endpoints not covered by the RPC interceptor or the MVC
/// filter. Place after UseRouting (via <see cref="TransactionMiddlewareExtensions.UsePechkaTransactions"/>)
/// so the [NoTransaction] endpoint metadata check works. Overlap with the other entry points is
/// harmless: their scopes join this one as nested scopes. Note that the commit happens after the
/// response has been written.
/// </summary>
public class PechkaTransactionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PechkaDbTransactionOptions _options;
    private readonly ILogger<PechkaTransactionMiddleware> _logger;

    public PechkaTransactionMiddleware(RequestDelegate next, PechkaDbTransactionOptions options,
        ILogger<PechkaTransactionMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<NoTransactionAttribute>() != null)
        {
            await _next(context);
            return;
        }
        var managers = context.RequestServices.GetServices<ITransactionalDbContextManager>().ToList();
        if (managers.Count == 0)
        {
            await _next(context);
            return;
        }

        await using var scopes = TransactionScopeSet.Begin(managers, _options, _logger,
            context.Request.Path);
        await _next(context);
        if (context.Response.StatusCode < 500)
            await scopes.CommitAsync();
    }
}

public static class TransactionMiddlewareExtensions
{
    public static IApplicationBuilder UsePechkaTransactions(this IApplicationBuilder app)
        => app.UseMiddleware<PechkaTransactionMiddleware>();
}
