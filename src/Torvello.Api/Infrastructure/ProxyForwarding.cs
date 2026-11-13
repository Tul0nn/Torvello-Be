using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Torvello.Api.Infrastructure;

public static class ProxyForwarding
{
    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.ForwardLimit = 1;
        options.ForwardedHeaders = ForwardedHeaders.None;
        foreach (var entry in configuration.GetSection("ForwardedHeaders:KnownProxies").GetChildren())
        {
            if (!IPAddress.TryParse(entry.Value, out var address))
                throw new InvalidOperationException($"{entry.Path} must be a valid IP address.");
            options.KnownProxies.Add(address);
        }
        foreach (var entry in configuration.GetSection("ForwardedHeaders:KnownNetworks").GetChildren())
        {
            if (!System.Net.IPNetwork.TryParse(entry.Value, out var network))
                throw new InvalidOperationException($"{entry.Path} must be a valid CIDR network.");
            options.KnownIPNetworks.Add(network);
        }
        if (options.KnownProxies.Count > 0 || options.KnownIPNetworks.Count > 0)
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    }
}
