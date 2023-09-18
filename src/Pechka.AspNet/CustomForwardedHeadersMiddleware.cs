using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Pechka.AspNet;

class CustomForwardedHeadersMiddleware : IMiddleware
{
    private readonly PechkaJsonConfig _jsonConfig;
    private readonly HashSet<IPAddress>? _validForwarders;

    public CustomForwardedHeadersMiddleware(PechkaJsonConfig jsonConfig)
    {
        _jsonConfig = jsonConfig;
        if (jsonConfig.Http?.KnownProxies != null)
            _validForwarders = new HashSet<IPAddress>(jsonConfig.Http.KnownProxies.Select(IPAddress.Parse));
    }

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Connection.RemoteIpAddress == null
            || _validForwarders == null
            || _validForwarders.Contains(context.Connection.RemoteIpAddress))
        {

            var proto = context.Request.Headers["X-Forwarded-Proto"];
            if (proto.Count != 0)
                context.Request.Scheme = proto;
            var forwardedFor = context.Request.Headers["X-Forwarded-For"];
            if (forwardedFor.Count != 0)
            {
                var s = forwardedFor[0];
                var commaIndex = s.IndexOf(',', StringComparison.Ordinal);
                if (commaIndex != -1)
                    s = s.Substring(0, commaIndex);
                context.Connection.RemoteIpAddress = IPAddress.Parse(s);
            }
        }

        return next(context);
    }
}