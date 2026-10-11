using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agentd.Domain.Common;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Artifacts;

/// <summary>What published an artifact: any agent turn (see docs/architect/artifacts.md §1).</summary>
public static class ArtifactSources
{
    public const string Job = "job";
    public const string Chat = "chat";
    public const string Idea = "idea";

    /// <summary>A PR review's thread (<c>!review</c>).</summary>
    public const string Review = "review";

    /// <summary>A review page (its reviewer, its questions, Fix it).</summary>
    public const string ReviewSession = "review-session";

    /// <summary>A watched PR's fix round.</summary>
    public const string Watch = "watch";

    /// <summary>Not an agent (seeding, tests).</summary>
    public const string Web = "web";
}

/// <summary>An artifact without its HTML, as the Web UI lists it. Its page is <c>/artifacts/{Name}.html</c>.</summary>
public sealed record ArtifactSummary(string Name, string Title, string Source, long SourceId, string Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string Storage = ArtifactStorage.Database);

/// <summary>Where an artifact's HTML is kept.</summary>
public static class ArtifactStorage
{
    public const string Database = "database";

    /// <summary>S3-compatible storage (Cloudflare R2), under <see cref="IArtifactBlobs"/>.</summary>
    public const string S3 = "s3";
}

/// <summary>
/// A published page. <see cref="Html"/> is in the row for <see cref="ArtifactStorage.Database"/>; an S3 one's is fetched
/// from <see cref="IArtifactBlobs"/>. An expired one (<see cref="ExpiredAt"/>) has none: its link says it expired.
/// </summary>
public sealed record Artifact(string Name, string Title, string? Html, string Source, long SourceId, string Author, DateTimeOffset UpdatedAt, DateTimeOffset? ExpiredAt = null,
    string Storage = ArtifactStorage.Database);

/// <summary>A page that just expired, and where it was stored.</summary>
public sealed record ExpiredArtifact(string Name, string Storage);

/// <summary><c>Agentd:Artifacts</c>.</summary>
public sealed class ArtifactOptions
{
    public const string Section = "Agentd:Artifacts";

    /// <summary>Days after its last update that a page expires (its HTML is dropped); 0 keeps pages forever.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>S3-compatible storage for new pages; unset keeps them in the database.</summary>
    public ArtifactS3Options S3 { get; set; } = new();
}

/// <summary>
/// <c>Agentd:Artifacts:S3</c>: e.g. Cloudflare R2. <see cref="Url"/> is the S3 API URL with the bucket as its path
/// (<c>https://&lt;account&gt;.r2.cloudflarestorage.com/agentd</c>). The keys are secrets: <c>agentd secrets set Artifacts:S3:AccessKeyId</c>
/// and <c>…:SecretAccessKey</c> (encrypted, never in agentd.json).
/// </summary>
public sealed class ArtifactS3Options
{
    public string? Url { get; set; }

    /// <summary>
    /// Where the bucket's pages are public, e.g. its custom domain <c>https://agentd.example.com</c>: an S3 page's link is then
    /// <c>{PublicUrl}/artifacts/&lt;name&gt;.html</c> instead of agentd's. Unset links every page to agentd.
    /// </summary>
    public string? PublicUrl { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    /// <summary>Everything needed to use it.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey);
}

/// <summary>Artifact pages in S3-compatible storage, one object per page (<c>artifacts/&lt;name&gt;.html</c>).</summary>
public interface IArtifactBlobs
{
    Task PutAsync(string name, string html, CancellationToken cancellationToken);

    /// <summary>The page, or null when the object isn't there.</summary>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken);

    Task DeleteAsync(string name, CancellationToken cancellationToken);
}

/// <summary>What <see cref="ArtifactService.ReadAsync"/> found: the page's HTML, or why there's none.</summary>
public sealed record ArtifactPage(Artifact Artifact, string? Html, bool Expired, bool Missing);

/// <summary>What publishing did: the page's name (its link) and whether it's new or replaced an earlier version.</summary>
public sealed record PublishedArtifact(string Name, bool Created, string Storage = ArtifactStorage.Database)
{
    public string Path => $"/artifacts/{Name}.html";
}

/// <summary>Artifacts via <c>agentd.artifact_*</c> routines.</summary>
public interface IArtifactStore
{
    /// <summary>
    /// Publishes as <paramref name="name"/>, or replaces the same source's page with this title (keeping its name). The
    /// HTML is in the row for <see cref="ArtifactStorage.Database"/>, null when the page is stored in S3.
    /// </summary>
    Task<PublishedArtifact> PublishAsync(string name, string title, string? html, string source, long sourceId, string author, string storage, CancellationToken cancellationToken);

    Task<Artifact?> GetAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactSummary>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Expires pages last updated before <paramref name="updatedBefore"/>; returns those that expired now.</summary>
    Task<IReadOnlyList<ExpiredArtifact>> ExpireAsync(DateTimeOffset updatedBefore, CancellationToken cancellationToken);
}

/// <summary>
/// Agents publish HTML pages (docs/architect/artifacts.md): each is served at <c>/artifacts/&lt;slug&gt;-&lt;hash&gt;.html</c> to
/// anyone who can open the Web UI, sandboxed so its scripts can't reach agentd. A job or chat publishing the same title
/// again updates the page in place.
/// </summary>
/// <remarks>With <see cref="ArtifactOptions.S3"/> configured, new pages go to S3-compatible storage and the row keeps the metadata.</remarks>
public sealed class ArtifactService(IArtifactStore store, IClock? clock = null, IOptions<ArtifactOptions>? options = null, IArtifactBlobs? blobs = null)
{
    public const int MaxTitle = 120;

    /// <summary>2 MB of HTML: plenty for a report with inline SVG, small enough for the database and the browser.</summary>
    public const int MaxHtml = 2 * 1024 * 1024;

    /// <param name="title">The page's title (also names its link).</param>
    /// <param name="html">A whole document or a body fragment.</param>
    /// <param name="source">One of <see cref="ArtifactSources"/>.</param>
    /// <param name="sourceId">The job's, chat's, idea's, … id.</param>
    /// <param name="author">Who published it, as shown (e.g. "job #12").</param>
    /// <param name="ct">Cancels it.</param>
    public async Task<Result<PublishedArtifact>> PublishAsync(string title, string html, string source, long sourceId, string author, CancellationToken ct)
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
        var document = Document(title.Trim(), html);
        if (blobs is null || options?.Value.S3.Configured != true)
        {
            return await store.PublishAsync(name, title.Trim(), document, source, sourceId, author, ArtifactStorage.Database, ct).ConfigureAwait(false);
        }

        // The row first: republishing keeps the first name, and the object is written under the name the row has.
        var published = await store.PublishAsync(name, title.Trim(), null, source, sourceId, author, ArtifactStorage.S3, ct).ConfigureAwait(false);
        try
        {
            await blobs.PutAsync(published.Name, document, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DomainError.Validation($"The page couldn't be uploaded to storage ({ex.Message}). Publish it again.");
        }

        return published with { Storage = ArtifactStorage.S3 };
    }

    /// <summary>
    /// A page's link: an S3 page's is on <see cref="ArtifactS3Options.PublicUrl"/> when that's set; any other is on agentd at
    /// <paramref name="origin"/> (<c>Agentd:Web:PublicOrigin</c>), or relative without one. agentd serves every page either way.
    /// </summary>
    public string Link(string name, string storage, string? origin)
    {
        var publicUrl = options?.Value.S3.PublicUrl;
        var root = storage == ArtifactStorage.S3 && Uri.TryCreate(publicUrl, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" ? publicUrl : origin;
        return (root?.TrimEnd('/') ?? string.Empty) + $"/artifacts/{name}.html";
    }

    /// <summary>A page to serve: its HTML from the row or from S3, or that it expired or is missing in storage.</summary>
    public async Task<ArtifactPage?> ReadAsync(string name, CancellationToken ct)
    {
        if (await store.GetAsync(name, ct).ConfigureAwait(false) is not { } artifact)
        {
            return null;
        }

        if (artifact.ExpiredAt is not null)
        {
            return new ArtifactPage(artifact, null, Expired: true, Missing: false);
        }

        var html = artifact.Storage == ArtifactStorage.S3
            ? blobs is null ? null : await blobs.GetAsync(artifact.Name, ct).ConfigureAwait(false)
            : artifact.Html;
        return new ArtifactPage(artifact, html, Expired: false, Missing: html is null);
    }

    /// <summary>
    /// Expires pages not updated for <see cref="ArtifactOptions.RetentionDays"/> (run by the hourly sweep). Their HTML is
    /// dropped; the link then answers that the page expired. Returns how many expired.
    /// </summary>
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        if (options?.Value.RetentionDays is not > 0 || clock is null)
        {
            return 0;
        }

        var expired = await store.ExpireAsync(clock.UtcNow.AddDays(-options.Value.RetentionDays), ct).ConfigureAwait(false);
        foreach (var page in expired.Where(e => e.Storage == ArtifactStorage.S3))
        {
            try
            {
                await (blobs?.DeleteAsync(page.Name, ct) ?? Task.CompletedTask).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // a leftover object only costs storage; the page is expired either way
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
            }
        }

        return expired.Count;
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
