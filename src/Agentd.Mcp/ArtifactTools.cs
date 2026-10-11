using System.ComponentModel;
using System.Globalization;
using Agentd.Application.Artifacts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Agentd.Mcp;

/// <summary>
/// <c>publish_artifact</c> for every agent (jobs, chats, ideas, reviews, follow-ups, fix rounds): an HTML page served by
/// agentd to anyone who can open the Web UI. The source comes from the bearer token, never from an argument.
/// </summary>
[McpServerToolType]
[Authorize(Policy = McpHosting.AgentPolicy)]
public sealed class ArtifactTools(IHttpContextAccessor http, ArtifactService artifacts, IConfiguration configuration)
{
    [McpServerTool(Name = "publish_artifact"), Description(
        "Publish an HTML page (a report, a diagram, an explainer, a comparison table) that people open in agentd's Web UI. " +
        "Send a whole document or just the body. Inline CSS and JS work, and scripts and styles may load from cdnjs.cloudflare.com, " +
        "cdn.jsdelivr.net or unpkg.com; the page can't fetch data at runtime, so put the data in the page. Publishing again with the " +
        "same title replaces the page and keeps its link. Returns the link to share.")]
    public async Task<string> PublishArtifact(
        [Description("A short title, e.g. 'Sync job: how a run pages through tables'. Also names the link.")] string title,
        [Description("The HTML: a complete document, or the content of <body>.")] string html,
        CancellationToken cancellationToken)
    {
        var (source, id) = Source(http.HttpContext?.User) ?? throw new McpException("Not authenticated as an agentd agent.");
        var result = await artifacts.PublishAsync(title, html, source, id, $"{source} #{id}", cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new McpException($"publish_artifact failed: {result.Error.Message}");
        }

        var link = artifacts.Link(result.Value.Name, result.Value.Storage, configuration["Agentd:Web:PublicOrigin"]);
        return result.Value.Created ? $"Published: {link}" : $"Updated (same link): {link}";
    }

    /// <summary>Which agent is publishing: the job's or chat's token, or a turn's artifact token (<c>idea:5</c>).</summary>
    private static (string Source, long Id)? Source(System.Security.Claims.ClaimsPrincipal? user)
    {
        if (Id(user?.FindFirst(McpJobAuthenticationHandler.JobIdClaim)?.Value) is { } job)
        {
            return (ArtifactSources.Job, job);
        }

        if (Id(user?.FindFirst(McpJobAuthenticationHandler.ChatIdClaim)?.Value) is { } chat)
        {
            return (ArtifactSources.Chat, chat);
        }

        return user?.FindFirst(McpJobAuthenticationHandler.ArtifactSourceClaim)?.Value is { } claim && claim.LastIndexOf(':') is > 0 and var at
            && Id(claim[(at + 1)..]) is { } id
            ? (claim[..at], id)
            : null;
    }

    private static long? Id(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
