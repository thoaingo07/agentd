using Agentd.Application.Permissions;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Claude;

/// <summary>Whether a rule key (e.g. <c>Bash(tail:*)</c>) is already in the agent's <c>--allowedTools</c>.</summary>
public sealed class ClaudeToolAllowlist(IOptions<ClaudeOptions> options) : IToolAllowlist
{
    public bool IsAllowed(string ruleKey) => options.Value.AllowedTools.Contains(ruleKey, StringComparer.Ordinal);
}
