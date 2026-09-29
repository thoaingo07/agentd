using Agentd.Host.Options;

namespace Agentd.Host;

internal static class HostUrls
{
    /// <summary>
    /// Listen on <c>Agentd:Web:Urls</c> (loopback by default) unless ASP.NET Core URLs are already
    /// configured (ASPNETCORE_URLS / --urls, e.g. set by Aspire or launch profiles) or Kestrel
    /// endpoints are configured explicitly.
    /// </summary>
    public static void ApplyDefault(WebApplicationBuilder builder)
    {
        var configured = !string.IsNullOrWhiteSpace(builder.Configuration["urls"])
                         || builder.Configuration.GetSection("Kestrel:Endpoints").Exists();
        if (configured)
        {
            return;
        }

        var web = builder.Configuration.GetSection($"{AgentdOptions.Section}:Web").Get<WebOptions>() ?? new WebOptions();
        builder.WebHost.UseUrls(web.Urls);
    }
}
