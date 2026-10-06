using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentd.Application.Permissions;

/// <summary>What a permission request is about, for people and for remembering approvals.</summary>
/// <param name="Summary">What the agent wants (the shell command, the URL, the tool).</param>
/// <param name="RuleKeys">Keys in Claude Code's allowlist syntax that "allow for this job / always" remembers, e.g. <c>Bash(npm install:*)</c>.</param>
/// <param name="HardDeny">Set when agentd never allows this, whoever answers.</param>
public sealed record PermissionAnalysis(string Summary, IReadOnlyList<string> RuleKeys, string? HardDeny);

/// <summary>
/// Turns a tool call into rule keys and applies the hard denies. A shell command is split into the simple commands it
/// runs (<see cref="ShellCommand"/>: quote-aware, including <c>$( … )</c>); each one's key is its command and subcommand
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

    /// <summary>Commands that only change the shell's state or test something; Claude Code doesn't ask about them.</summary>
    private static readonly HashSet<string> s_neutral = new(StringComparer.Ordinal) { "cd", "pushd", "popd", "true", "false", ":", "export", "test", "[", "[[" };

    /// <summary>Words that start a command without being one: <c>then npm test</c> runs <c>npm test</c>.</summary>
    private static readonly HashSet<string> s_prefixes = new(StringComparer.Ordinal) { "do", "then", "else", "elif", "if", "while", "until", "!", "time", "{", "nohup", "exec" };

    /// <summary>
    /// Tools whose second word picks what they do (<c>git push</c> vs <c>git log</c>), so it's part of the key. For any
    /// other command the rest is data (<c>echo hello</c>, <c>which node</c>) and the key is the command alone.
    /// </summary>
    private static readonly HashSet<string> s_withSubcommands = new(StringComparer.Ordinal)
    {
        "git", "npm", "npx", "yarn", "pnpm", "dotnet", "docker", "kubectl", "helm", "az", "gh", "cargo", "go", "pip", "pip3",
        "terraform", "systemctl", "apt", "apt-get", "brew", "make", "mvn", "gradle", "ng",
    };

    /// <summary>Lines of shell syntax that run nothing themselves (a loop header's commands come from its $( … ) parts).</summary>
    private static readonly HashSet<string> s_syntax = new(StringComparer.Ordinal) { "for", "case", "select", "in", "done", "fi", "esac", "}", "function" };

    public static PermissionAnalysis Analyze(string toolName, string inputJson)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        var input = Parse(inputJson);
        switch (toolName)
        {
            case "Bash":
                var command = Text(input, "command") ?? string.Empty;
                var deny = s_hardDenies.FirstOrDefault(d => d.Pattern.IsMatch(command)).Reason;
                var keys = ShellCommand.Split(command).Select(CommandKey).OfType<string>().Distinct(StringComparer.Ordinal).Select(k => $"Bash({k}:*)").ToList();
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

    /// <summary>
    /// "npm install --no-audit" → "npm install"; "python3 x.py" → "python3"; "FOO=1 make" → "make"; "then npm test" → "npm test";
    /// "cd x", "for f in *.txt", "done" → null.
    /// </summary>
    internal static string? CommandKey(string part)
    {
        var words = part.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && (s_prefixes.Contains(words[0]) || EnvAssignment().IsMatch(words[0])))
        {
            words.RemoveAt(0);
        }

        if (words.Count == 0 || s_neutral.Contains(words[0]) || s_syntax.Contains(words[0]))
        {
            return null;
        }

        var second = words.Count > 1 ? words[1] : null;
        return second is not null && s_withSubcommands.Contains(words[0]) && SubCommand().IsMatch(second) ? $"{words[0]} {second}" : words[0];
    }

    /// <summary>Whether <paramref name="granted"/> covers <paramref name="key"/>: the key itself, or its command's broader key
    /// (<c>Bash(npm:*)</c> covers <c>Bash(npm install:*)</c>), as Claude Code's prefix rules do.</summary>
    public static bool Covers(Func<string, bool> granted, string key)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(key);
        if (granted(key))
        {
            return true;
        }

        var match = BashKey().Match(key);
        return match.Success && match.Groups["sub"].Success && granted($"Bash({match.Groups["cmd"].Value}:*)");
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

    [GeneratedRegex(@"^Bash\((?<cmd>[^ :]+)(?<sub> [^:]+)?:\*\)$")]
    private static partial Regex BashKey();

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
