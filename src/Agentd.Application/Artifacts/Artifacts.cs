using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agentd.Domain.Common;

namespace Agentd.Application.Artifacts;

/// <summary>An artifact without its HTML, as the Web UI lists it. Its page is <c>/artifacts/{Name}.html</c>.</summary>
public sealed record ArtifactSummary(string Name, string Title, long? JobId, long? ChatId, string Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A published page.</summary>
public sealed record Artifact(string Name, string Title, string Html, long? JobId, long? ChatId, string Author, DateTimeOffset UpdatedAt);

/// <summary>What publishing did: the page's name (its link) and whether it's new or replaced an earlier version.</summary>
public sealed record PublishedArtifact(string Name, bool Created)
{
    public string Path => $"/artifacts/{Name}.html";
}

/// <summary>Artifacts via <c>agentd.artifact_*</c> routines.</summary>
public interface IArtifactStore
{
    /// <summary>Publishes as <paramref name="name"/>, or replaces the same source's page with this title (keeping its name).</summary>
    Task<PublishedArtifact> PublishAsync(string name, string title, string html, long? jobId, long? chatId, string author, CancellationToken cancellationToken);

    Task<Artifact?> GetAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactSummary>> ListAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Agents publish HTML pages (docs/architect/artifacts.md): each is served at <c>/artifacts/&lt;slug&gt;-&lt;hash&gt;.html</c> to
/// anyone who can open the Web UI, sandboxed so its scripts can't reach agentd. A job or chat publishing the same title
/// again updates the page in place.
/// </summary>
public sealed class ArtifactService(IArtifactStore store)
{
    public const int MaxTitle = 120;

    /// <summary>2 MB of HTML: plenty for a report with inline SVG, small enough for the database and the browser.</summary>
    public const int MaxHtml = 2 * 1024 * 1024;

    public async Task<Result<PublishedArtifact>> PublishAsync(string title, string html, long? jobId, long? chatId, string author, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitle)
        {
            return DomainError.Validation($"Give the artifact a title (up to {MaxTitle} characters).");
        }

        if (string.IsNullOrWhiteSpace(html) || html.Length > MaxHtml)
        {
            return DomainError.Validation($"The artifact needs HTML (up to {MaxHtml / 1024 / 1024} MB).");
        }

        var name = $"{Slug(title)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        return await store.PublishAsync(name, title.Trim(), Document(title.Trim(), html), jobId, chatId, author, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lower-case ASCII words joined by dashes, at most 48 characters; "artifact" when nothing is left. Other letters are
    /// left out (the daemon runs without ICU, so there's no transliteration); the hash keeps names apart anyway.
    /// </summary>
    internal static string Slug(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (!char.IsLetterOrDigit(c) && sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString(0, Math.Min(sb.Length, 48)).Trim('-');
        return slug.Length > 0 ? slug : "artifact";
    }

    /// <summary>A whole document stays as it is; a fragment gets a minimal page around it.</summary>
    internal static string Document(string title, string html) =>
        html.Contains("<html", StringComparison.OrdinalIgnoreCase)
            ? html
            : $"<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
              $"<title>{WebUtility.HtmlEncode(title)}</title>\n</head>\n<body>\n{html}\n</body>\n</html>\n";
}
