using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentd.Application.Permissions;

/// <summary>What a permission request is about, for people and for remembering approvals.</summary>
/// <param name="Summary">What the agent wants (the shell command, the URL, the tool).</param>
/// <param name="RuleKeys">Keys in Claude Code's allowlist syntax that "allow for this job / always" remembers, e.g. <c>Bash(npm install:*)</c>.</param>
/// <param name="HardDeny">Set when agentd never allows this, whoever answers.</param>
public sealed record PermissionAnalysis(string Summary, IReadOnlyList<string> RuleKeys, string? HardDeny);

/// <summary>
/// Turns a tool call into rule keys and applies the hard denies. A shell command is split into its parts
/// (<c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>); each part's key is its command and subcommand
/// (<c>npm install</c>, <c>git fetch</c>), so approving one command approves that kind of command.
/// </summary>
public static partial class PermissionRules
{
    private static readonly (Regex Pattern, string Reason)[] s_hardDenies =
    [
        (GitPush(), "pushing is done by agentd, never by the agent"),
        (Sudo(), "no root access"),
        (PipeToShell(), "running a downloaded script"),
        (RemoveRoot(), "deleting outside the worktree"),
        (AgentdHome(), "agentd's own files and secrets"),
    ];

    /// <summary>Commands that only change the shell's state; Claude Code doesn't ask about them.</summary>
    private static readonly HashSet<string> s_neutral = new(StringComparer.Ordinal) { "cd", "pushd", "popd", "true", "export" };

    public static PermissionAnalysis Analyze(string toolName, string inputJson)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        var input = Parse(inputJson);
        switch (toolName)
        {
            case "Bash":
                var command = Text(input, "command") ?? string.Empty;
                var deny = s_hardDenies.FirstOrDefault(d => d.Pattern.IsMatch(command)).Reason;
                var keys = SplitCommand(command).Select(CommandKey).OfType<string>().Distinct(StringComparer.Ordinal).Select(k => $"Bash({k}:*)").ToList();
                return new PermissionAnalysis(command.Trim(), keys, deny);
            case "WebFetch":
                var url = Text(input, "url") ?? string.Empty;
                var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
                return new PermissionAnalysis(url, [$"WebFetch(domain:{host})"], null);
            default:
                var path = Text(input, "file_path") ?? Text(input, "path") ?? Text(input, "pattern");
                var hidden = path is not null && AgentdHome().IsMatch(path) ? "agentd's own files and secrets" : null;
                return new PermissionAnalysis(path is null ? toolName : $"{toolName} {path}", [toolName], hidden);
        }
    }

    /// <summary>The parts of a shell command (quotes are not parsed: a quoted separator splits too, which only asks more, never less).</summary>
    internal static IEnumerable<string> SplitCommand(string command) =>
        Separators().Split(command).Select(p => p.Trim()).Where(p => p.Length > 0);

    /// <summary>"npm install --no-audit" → "npm install"; "python3 x.py" → "python3"; "FOO=1 make" → "make"; "cd x" → null.</summary>
    internal static string? CommandKey(string part)
    {
        var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(w => EnvAssignment().IsMatch(w)).ToList();
        if (words.Count == 0 || s_neutral.Contains(words[0]))
        {
            return null;
        }

        var second = words.Count > 1 ? words[1] : null;
        return second is not null && SubCommand().IsMatch(second) ? $"{words[0]} {second}" : words[0];
    }

    private static JsonElement Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"\s*(?:&&|\|\||;|\|)\s*")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex EnvAssignment();

    /// <summary>A subcommand: a plain word (no option dash, path, dot or variable).</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9-]*$")]
    private static partial Regex SubCommand();

    [GeneratedRegex(@"(^|[\s;&|(])git\s+push\b")]
    private static partial Regex GitPush();

    [GeneratedRegex(@"(^|[\s;&|(])sudo\b")]
    private static partial Regex Sudo();

    [GeneratedRegex(@"\b(curl|wget)\b[^|;&]*\|\s*(sudo\s+)?(sh|bash|zsh|python3?)\b")]
    private static partial Regex PipeToShell();

    [GeneratedRegex(@"\brm\s+-[A-Za-z]*[rR][A-Za-z]*\s+(/|~|\$HOME)(\s|$|/\*?\s|/\*?$)")]
    private static partial Regex RemoveRoot();

    [GeneratedRegex(@"(^|[\s/""'=])\.agentd(/|\b)|AGENTD_HOME")]
    private static partial Regex AgentdHome();
}
