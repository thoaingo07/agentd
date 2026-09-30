using System.Globalization;
using Agentd.Application.Ports;

namespace Agentd.Infrastructure.Claude;

/// <summary>Command-line arguments for one agent turn (each an ArgumentList entry; no shell).</summary>
public static class ClaudeArgs
{
    public static IReadOnlyList<string> Build(AgentRunRequest request, ClaudeOptions options, string? mcpConfigPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<string>
        {
            "-p", request.Prompt,
            request.Resume ? "--resume" : "--session-id", request.Session.ToString(),
            "--output-format", "stream-json", "--verbose",
            "--permission-mode", options.PermissionMode,
            "--max-turns", options.MaxTurns.ToString(CultureInfo.InvariantCulture),
            "--append-system-prompt", options.SystemPromptRules,
        };
        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            args.Add("--model");
            args.Add(options.Model);
        }

        if (options.AllowedTools.Count > 0)
        {
            // One comma-separated value, so a variadic flag can't swallow other arguments.
            args.Add("--allowedTools");
            args.Add(string.Join(",", options.AllowedTools));
        }

        if (mcpConfigPath is not null)
        {
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);
        }

        return args;
    }
}
