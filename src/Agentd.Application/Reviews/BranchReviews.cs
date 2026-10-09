using System.Collections.Concurrent;
using System.Globalization;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Reviews;

/// <summary>Configuration section <c>Agentd:Web</c>, as far as links in chat need it.</summary>
public sealed class WebLinkOptions
{
    public const string Section = "Agentd:Web";

    /// <summary>The URL people open agentd on (behind a proxy), e.g. <c>https://agentd.example:18008</c>.</summary>
    public string? PublicOrigin { get; set; }

    /// <summary>A page's full link when the public URL is known, else its path.</summary>
    public string Link(string path) => (PublicOrigin?.TrimEnd('/') ?? string.Empty) + path;
}

/// <summary>
/// <c>!review branch:&lt;name&gt; [--base &lt;branch&gt;] [--repo &lt;name&gt;]</c> in chat (docs/architect/review-sessions.md §3): starts a
/// review session on the branch, answers with its page, and says in the same conversation when the findings are in.
/// </summary>
public sealed class BranchReviews(ReviewSessionService sessions, IRepositoryRegistry repositories, IMessagingProviderRegistry providers, IOptions<WebLinkOptions>? web = null)
{
    public const string Prefix = "branch:";

    private readonly ConcurrentDictionary<long, ConversationRef> _watching = new();

    public static bool Matches(IReadOnlyList<string> args) =>
        args is [var first, ..] && first.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public async Task<Result<string>> StartAsync(ConversationRef where, string author, IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        var branch = args.Count > 0 && Matches(args) ? args[0][Prefix.Length..].Trim() : string.Empty;
        string? baseBranch = null, repoName = null;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--base" when i + 1 < args.Count:
                    baseBranch = args[++i];
                    break;
                case "--repo" when i + 1 < args.Count:
                    repoName = args[++i];
                    break;
                default:
                    return DomainError.Validation($"I don't know `{args[i]}`. Use `!review branch:<name> [--base <branch>] [--repo <name>]`.");
            }
        }

        if (branch.Length == 0)
        {
            return DomainError.Validation("Which branch? Use `!review branch:<name> [--base <branch>] [--repo <name>]`.");
        }

        if (repoName is null)
        {
            var all = await repositories.ListAsync(ct).ConfigureAwait(false);
            if (all.Count != 1)
            {
                return DomainError.Validation(all.Count == 0
                    ? "No repository is registered yet: an admin can add one with `!repo add <clone url>`."
                    : $"Several repositories are registered: add `--repo <name>` ({string.Join(", ", all.Select(r => r.Name.Value))}).");
            }

            repoName = all[0].Name.Value;
        }

        var started = await sessions.StartAsync(new StartReview(repoName, Branch: branch, Base: baseBranch), author, ct).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return started.Error;
        }

        var s = started.Value!;
        _watching[s.Id] = where;
        return string.Create(CultureInfo.InvariantCulture,
            $"🔍 Reviewing `{s.HeadRef}` → `{s.BaseRef}` in {s.Repository} (review #{s.Id}). I'll say here when the findings are in. The page: {Page(s.Id)}");
    }

    /// <summary>Tells the conversation that started a review how it went (once; nothing for reviews started elsewhere).</summary>
    public async Task NotifyAsync(ReviewSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!_watching.TryRemove(s.Id, out var where))
        {
            return;
        }

        var breaks = s.Findings.Count(f => f.Severity == ReviewFindings.Breaks);
        var text = s.Status == ReviewSessionStatus.Failed
            ? $"⚠️ The review of `{s.HeadRef}` failed: {s.Error} {Page(s.Id)}"
            : string.Create(CultureInfo.InvariantCulture,
                $"📝 The review of `{s.HeadRef}` → `{s.BaseRef}` is ready: 🔴 {breaks} 🟠 {s.Findings.Count - breaks}. {s.Summary} Keep, edit or drop them, comment and ask on the page: {Page(s.Id)}");
        var provider = providers.Resolve(where.Provider);
        foreach (var part in MessageChunker.Prepare(new OutboundMessage(MessageKind.Info, text), provider.Capabilities))
        {
            await provider.SendAsync(where, part, ct).ConfigureAwait(false);
        }
    }

    private string Page(long id) => (web?.Value ?? new WebLinkOptions()).Link($"/reviews/{id}");
}
