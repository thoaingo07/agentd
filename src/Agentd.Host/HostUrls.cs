using System.Net;
using Agentd.Host.Configuration;
using Agentd.Host.Options;

namespace Agentd.Host;

internal static class HostUrls
{
    /// <summary>
    /// Listen on <c>Agentd:Web:Urls</c> (loopback by default) unless ASP.NET Core URLs are already
    /// configured (ASPNETCORE_URLS / --urls, e.g. set by Aspire or launch profiles) or Kestrel
    /// endpoints are configured explicitly. Until setup is complete it's loopback only, whatever
    /// <c>Web:Urls</c> says: the setup link is the only way in (docs/architect/deployment.md §5a).
    /// </summary>
    public static void ApplyDefault(WebApplicationBuilder builder, ConfigHome home)
    {
        var configured = !string.IsNullOrWhiteSpace(builder.Configuration["urls"])
                         || builder.Configuration.GetSection("Kestrel:Endpoints").Exists();
        if (configured)
        {
            return;
        }

        var web = builder.Configuration.GetSection($"{AgentdOptions.Section}:Web").Get<WebOptions>() ?? new WebOptions();
        builder.WebHost.UseUrls(SetupState.IsCompleteIn(home, builder.Configuration) ? web.Urls : Loopback(web.Urls));
    }

    /// <summary>The same URLs on 127.0.0.1 (same scheme and port); loopback ones stay as they are.</summary>
    public static string Loopback(string urls) =>
        string.Join(';', urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(LoopbackUrl));

    private static string LoopbackUrl(string url)
    {
        // "http://*:7780" / "http://+:7780" mean every interface.
        var normalized = url.Replace("://*", "://wildcard.invalid", StringComparison.Ordinal).Replace("://+", "://wildcard.invalid", StringComparison.Ordinal);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) || IsLoopback(parsed.Host))
        {
            return url;
        }

        var port = parsed.IsDefaultPort ? string.Empty : $":{parsed.Port}";
        return $"{parsed.Scheme}://127.0.0.1{port}";
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
