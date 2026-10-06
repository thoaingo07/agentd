using System.Collections;

namespace Agentd.Infrastructure.Claude;

/// <summary>Builds an agent process environment without agentd's secrets or infrastructure settings.</summary>
public static class SafeEnvironment
{
    private static readonly string[] s_removedPrefixes =
    [
        "Agentd__", "AGENTD_", "ConnectionStrings__", "ASPNETCORE_", "DOTNET_", "OTEL_", "AZURE_", "services__",
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "AZURE_DEVOPS_EXT_PAT",
    ];

    public static Dictionary<string, string> Build(IDictionary current, ClaudeOptions options)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(options);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in current)
        {
            var key = (string)entry.Key;
            if (!s_removedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                env[key] = entry.Value?.ToString() ?? string.Empty;
            }
        }

        // Subscription auth only (no API key): a dedicated login dir and/or a long-lived token.
        // A hung command ends with a timeout instead of blocking the job (it would otherwise read no messages until it ends).
        env["BASH_MAX_TIMEOUT_MS"] = ((long)options.CommandTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(options.ConfigDir))
        {
            env["CLAUDE_CONFIG_DIR"] = Paths.Expand(options.ConfigDir);
        }

        // A permission question may wait minutes for a person; the CLI's MCP call must not give up first.
        env["MCP_TOOL_TIMEOUT"] = ((long)options.McpToolTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(options.OAuthToken))
        {
            env[options.OAuthTokenVariable] = options.OAuthToken;
        }

        return env;
    }
}

internal static class Paths
{
    public static string Expand(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/', '\\'))
            : path;
}
