using Agentd.Infrastructure.Claude;
using Agentd.Mcp;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>
/// When <c>Agentd:Claude:McpUrl</c> is not configured, points agents at this host's own <c>/mcp</c>
/// endpoint (first HTTP address, wildcard hosts mapped to loopback). Resolved on first use, after the server started.
/// </summary>
internal sealed class McpUrlFromServer(IServer server) : IPostConfigureOptions<ClaudeOptions>
{
    public void PostConfigure(string? name, ClaudeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.McpUrl))
        {
            return;
        }

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? addresses.FirstOrDefault();
        if (address is not null)
        {
            options.McpUrl = For(address);
        }
    }

    internal static string For(string address)
    {
        var uri = new UriBuilder(address.Replace("://+", "://0.0.0.0", StringComparison.Ordinal).Replace("://*", "://0.0.0.0", StringComparison.Ordinal));
        if (uri.Host is "0.0.0.0" or "[::]" or "::")
        {
            uri.Host = "127.0.0.1";
        }

        uri.Path = McpHosting.Path;
        return uri.Uri.ToString();
    }
}
