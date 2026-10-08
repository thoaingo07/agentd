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
        // The step's model (Agentd:Jobs:Steps) over the default (Agentd:Claude:Model); the session itself stays the same.
        if ((request.Model ?? options.Model) is { Length: > 0 } model)
        {
            args.Add("--model");
            args.Add(model);
        }

        if (request.Effort is { Length: > 0 } effort)
        {
            args.Add("--effort");
            args.Add(effort);
        }

        // Until the plan is approved the agent may only read: no edit, commit or build tools are allowed.
        var tools = request.ReadOnly ? options.ReadOnlyTools : options.AllowedTools;
        if (tools.Count > 0)
        {
            // One comma-separated value, so a variadic flag can't swallow other arguments.
            args.Add("--allowedTools");
            args.Add(string.Join(",", tools));
        }

        if (request.ReadOnly)
        {
            // Explicitly deny edits too: allowedTools only auto-approves; acceptEdits would still allow edits.
            args.Add("--disallowedTools");
            args.Add("Edit,Write,MultiEdit,NotebookEdit");
        }

        // Only agentd's MCP server: never the operator's own MCP config or claude.ai connectors (Drive, Docs, …),
        // which a subscription login would otherwise load into the agent's session.
        args.Add("--strict-mcp-config");

        if (mcpConfigPath is not null)
        {
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);

            // Tool calls outside the allowlist are sent to agentd (a person approves or denies) instead of refused.
            if (!string.IsNullOrWhiteSpace(options.PermissionPromptTool))
            {
                args.Add("--permission-prompt-tool");
                args.Add(options.PermissionPromptTool);
            }
        }

        return args;
    }
}
