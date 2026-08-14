using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Pechka.AspNet.Database;

/// <summary>
/// Re-runs the downstream pipeline when a request's unit of work fails with a transient database
/// error (MVC filters cannot re-invoke an action, so retries happen at pipeline level). Place
/// after UseRouting (via <see cref="TransactionRetryMiddlewareExtensions.UsePechkaTransactionRetries"/>)
/// so endpoint metadata is available before the first attempt: only retry-eligible endpoints get
/// their request body buffered for replay. No-op unless PechkaDbTransactionOptions.EnableRetries
/// is set; opt endpoints out with [NoRetry] (or [NoTransaction]). Requests whose response has
/// already started are never retried. When combined with UsePechkaTransactions, place this one first.
/// </summary>
public class PechkaTransactionRetryMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PechkaDbTransactionOptions _options;
    private readonly ILogger<PechkaTransactionRetryMiddleware> _logger;

    public PechkaTransactionRetryMiddleware(RequestDelegate next, PechkaDbTransactionOptions options,
        ILogger<PechkaTransactionRetryMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (!_options.EnableRetries
            || endpoint == null
            || endpoint.Metadata.GetMetadata<NoRetryAttribute>() != null
            || endpoint.Metadata.GetMetadata<NoTransactionAttribute>() != null)
        {
            await _next(context);
            return;
        }

        var request = context.Request;
        var mayHaveBody = request.ContentLength > 0
            || (request.ContentLength == null && !HttpMethods.IsGet(request.Method)
                                              && !HttpMethods.IsHead(request.Method));
        if (mayHaveBody)
            request.EnableBuffering();

        await TransactionRetry.ExecuteAsync<object?>(_options, _logger,
            endpoint.DisplayName ?? request.Path,
            async attemptNo =>
            {
                if (attemptNo > 1)
                {
                    context.Response.Clear();
                    if (request.Body.CanSeek)
                        request.Body.Position = 0;
                }
                await _next(context);
                return null;
            },
            canRetry: _ => !context.Response.HasStarted,
            token: context.RequestAborted);
    }
}

public static class TransactionRetryMiddlewareExtensions
{
    public static IApplicationBuilder UsePechkaTransactionRetries(this IApplicationBuilder app)
        => app.UseMiddleware<PechkaTransactionRetryMiddleware>();
}
