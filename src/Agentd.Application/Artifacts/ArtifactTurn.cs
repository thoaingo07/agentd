using Agentd.Application.Ideas;
using Agentd.Application.Ports;

namespace Agentd.Application.Artifacts;

/// <summary>
/// Runs one agent turn with an artifact-only MCP token (<c>publish_artifact</c>, as <c>source</c> <c>id</c>): issued before
/// the turn and revoked after it. A turn that brings its own token (a chat's) keeps it; without an issuer it runs as before.
/// </summary>
public static class ArtifactTurn
{
    public static async Task<BrainstormReply> RunAsync(IBrainstormAgent agent, IMcpTokenIssuer? tokens, string source, long id, BrainstormTurn turn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(turn);
        var token = turn.McpToken is null ? tokens?.IssueArtifacts(source, id) : null;
        try
        {
            return await agent.RunAsync(token is null ? turn : turn with { McpToken = token }, ct).ConfigureAwait(false);
        }
        finally
        {
            if (token is not null)
            {
                tokens!.RevokeToken(token);
            }
        }
    }
}
