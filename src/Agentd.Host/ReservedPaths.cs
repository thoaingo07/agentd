namespace Agentd.Host;

/// <summary>Server-owned path prefixes that must never be answered by the SPA fallback.</summary>
internal static class ReservedPaths
{
    private static readonly string[] s_prefixes = ["/api", "/bff", "/hubs", "/mcp"];

    public static void MapNotFound(WebApplication app)
    {
        foreach (var prefix in s_prefixes)
        {
            // Lower-precedence catch-alls: real endpoints under these prefixes (added in later phases) win.
            app.Map($"{prefix}/{{**rest}}", () => Results.NotFound()).WithOrder(int.MaxValue - 1);
            app.Map(prefix, () => Results.NotFound()).WithOrder(int.MaxValue - 1);
        }
    }
}
