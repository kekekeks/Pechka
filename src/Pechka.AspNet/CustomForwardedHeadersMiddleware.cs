using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Pechka.AspNet;

class CustomForwardedHeadersMiddleware : IMiddleware
{
    /// <summary>A trusted forwarder network: base address bytes + prefix length.</summary>
    private readonly record struct Forwarder(byte[] Network, int PrefixBits, AddressFamily Family)
    {
        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != Family)
                return false;
            var bytes = address.GetAddressBytes();
            var fullBytes = PrefixBits / 8;
            for (var i = 0; i < fullBytes; i++)
                if (bytes[i] != Network[i])
                    return false;
            var remainingBits = PrefixBits % 8;
            if (remainingBits == 0)
                return true;
            var mask = (byte)(0xFF << (8 - remainingBits));
            return (bytes[fullBytes] & mask) == (Network[fullBytes] & mask);
        }
    }

    private readonly PechkaJsonConfig _jsonConfig;
    private readonly Forwarder[]? _validForwarders;

    public CustomForwardedHeadersMiddleware(PechkaJsonConfig jsonConfig)
    {
        _jsonConfig = jsonConfig;
        if (jsonConfig.Http?.KnownProxies != null)
            _validForwarders = jsonConfig.Http.KnownProxies.Select(ParseForwarder).ToArray();
    }

    /// <summary>A KnownProxies entry is either an exact IP or CIDR notation (e.g. 10.0.0.0/8).</summary>
    private static Forwarder ParseForwarder(string entry)
    {
        var slash = entry.IndexOf('/');
        var address = IPAddress.Parse(slash < 0 ? entry : entry.Substring(0, slash));
        var bytes = address.GetAddressBytes();
        var prefixBits = slash < 0 ? bytes.Length * 8 : int.Parse(entry.Substring(slash + 1));
        return new Forwarder(bytes, prefixBits, address.AddressFamily);
    }

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Connection.RemoteIpAddress == null
            || _validForwarders == null
            || _validForwarders.Any(n => n.Contains(context.Connection.RemoteIpAddress)))
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