using Agentd.Application.Setup;
using Agentd.Bff.Setup;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Agentd.Host;

/// <summary>The one-time setup link: <c>http://127.0.0.1:7780/setup?token=…</c>.</summary>
internal static class SetupLink
{
    public const string DefaultBase = "http://127.0.0.1:7780";

    /// <summary>The link on the first http(s) address (wildcards as 127.0.0.1), or the default address.</summary>
    public static string For(IEnumerable<string> addresses, string token)
    {
        var address = addresses.Select(a => HostUrls.Loopback(a)).FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? DefaultBase;
        return $"{address.TrimEnd('/')}{SetupSession.PagePath}?token={Uri.EscapeDataString(token)}";
    }

    public static string Hint(string link) =>
        $"Open {link} to set agentd up (from another computer: ssh -L 7780:127.0.0.1:7780 <this server>, then open it there), or run `agentd init`. " +
        "The link works until setup is complete; `agentd setup-link` prints a new one.";
}

/// <summary>Until setup is complete, each start issues a new setup token and logs the link once the server listens.</summary>
internal sealed partial class SetupLinkAnnouncer(ISetupState state, SetupToken tokens, IServer server, IHostApplicationLifetime lifetime, ILogger<SetupLinkAnnouncer> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (state.IsComplete)
        {
            return Task.CompletedTask;
        }

        var token = tokens.Issue();
        lifetime.ApplicationStarted.Register(() =>
            LogSetupLink(logger, SetupLink.Hint(SetupLink.For(server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [], token))));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "agentd isn't set up yet. {Hint}")]
    private static partial void LogSetupLink(ILogger logger, string hint);
}
