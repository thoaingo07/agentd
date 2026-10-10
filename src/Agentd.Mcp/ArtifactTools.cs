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
/// <c>publish_artifact</c> for jobs and chats: an HTML page served by agentd to anyone who can open the Web UI. The source
/// (the job or the chat) comes from the bearer token, never from an argument.
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
        var user = http.HttpContext?.User;
        var jobId = Id(user?.FindFirst(McpJobAuthenticationHandler.JobIdClaim)?.Value);
        var chatId = jobId is null ? Id(user?.FindFirst(McpJobAuthenticationHandler.ChatIdClaim)?.Value) : null;
        if (jobId is null && chatId is null)
        {
            throw new McpException("Not authenticated as an agentd job or chat.");
        }

        var author = jobId is { } j ? $"job #{j}" : $"chat #{chatId}";
        var result = await artifacts.PublishAsync(title, html, jobId, chatId, author, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new McpException($"publish_artifact failed: {result.Error.Message}");
        }

        var link = (configuration["Agentd:Web:PublicOrigin"]?.TrimEnd('/') ?? string.Empty) + result.Value.Path;
        return result.Value.Created ? $"Published: {link}" : $"Updated (same link): {link}";
    }

    private static long? Id(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
