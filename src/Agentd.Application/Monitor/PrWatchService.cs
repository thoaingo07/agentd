using System.Globalization;
using System.Text.RegularExpressions;
using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Monitor;

/// <summary>
/// <c>!watch &lt;PR&gt;</c> / <c>!unwatch &lt;PR&gt;</c> (docs/architect/pr-reviewer-and-monitor.md §2.0): watching opens a 👀 thread for the PR
/// where the monitor reports and asks before pushing. Comments already on the PR when watching begins don't count.
/// </summary>
public sealed partial class PrWatchService(IPrWatchStore watches, IRepositoryRegistry repositories, IPullRequestService pullRequests, IMessagingProviderRegistry providers,
    IPrFixRounds? rounds = null)
{
    public const string Usage = "Use `!watch <PR url or id> [--repo r]`.";

    public async Task<Result<string>> WatchAsync(ProviderKey provider, string author, IReadOnlyList<string> args, CancellationToken ct)
    {
        var target = await ResolveAsync(args, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var (repo, id) = target.Value;
        if (await pullRequests.GetAsync(repo, id, ct).ConfigureAwait(false) is not { } pr)
        {
            return DomainError.NotFound($"PR !{id} in `{repo.Name}`");
        }

        if (pr.Status != PullRequestStatus.Active)
        {
            return DomainError.Validation($"PR !{id} is {pr.Status.ToString().ToLowerInvariant()}, so there's nothing to watch.");
        }

        if (await watches.FindActiveAsync(repo.Name.Value, id, ct).ConfigureAwait(false) is { } existing)
        {
            return $"👀 PR !{id} is already watched (since {existing.CreatedAt:yyyy-MM-dd}, by {existing.WatchedBy}): see its thread.";
        }

        var seen = (await pullRequests.ListCommentsAsync(repo, id, ct).ConfigureAwait(false)).Select(c => c.CommentId).ToList();
        var opening = new OutboundMessage(MessageKind.Info,
            $"👀 **Watching PR !{id}**: {pr.Title}\n`{pr.SourceBranch}` → `{pr.TargetBranch}` · {pr.Url}\n\n" +
            "I'll fix **failed PR builds**, **merge conflicts** and **new comments** from people I know. I prepare each fix and ask here " +
            "before pushing anything: **1** push · **2** discard. Say **unwatch** to stop.");
        var title = $"PR !{id}: {pr.Title}";
        var thread = await providers.Resolve(provider).OpenConversationAsync(
            new ConversationSpec(default, default, title, repo.Name, opening, $"👀 {IdeaService.Title(title)}"), ct).ConfigureAwait(false);
        var (_, created) = await watches.InsertAsync(repo.Name.Value, id, pr.Title, author, provider, thread.ExternalConversationId, thread.ExternalSpaceId, seen, ct).ConfigureAwait(false);
        return created ? $"👀 Watching PR !{id} in its thread." : $"👀 PR !{id} was just watched by someone else: see its thread.";
    }

    public async Task<Result<string>> UnwatchAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var target = await ResolveAsync(args, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var (repo, id) = target.Value;
        return await watches.FindActiveAsync(repo.Name.Value, id, ct).ConfigureAwait(false) is { } watch
            ? await StopAsync(watch, ct).ConfigureAwait(false)
            : DomainError.NotFound($"A watch on PR !{id} in `{repo.Name}`");
    }

    /// <summary>
    /// A message in a 👀 thread (the reply is posted there): <c>unwatch</c> stops; a prepared fix is answered with <b>1</b> / push
    /// or <b>2</b> / discard (by <paramref name="author"/>).
    /// </summary>
    public async Task<string> HandleThreadMessageAsync(PrWatch watch, string text, string author, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(watch);
        var answer = (text ?? string.Empty).Trim().TrimEnd('.', '!').ToLowerInvariant();
        if (answer is "unwatch" or "stop")
        {
            if (watch.Pending is not null && rounds is not null)
            {
                await rounds.DiscardAsync(watch, "you stopped watching", ct).ConfigureAwait(false);
            }

            return await StopAsync(watch, ct).ConfigureAwait(false);
        }

        if (watch.Pending is not null && rounds is not null)
        {
            if (answer is "1" or "push" or "yes")
            {
                return await rounds.PushAsync(watch, author, ct).ConfigureAwait(false);
            }

            if (answer is "2" or "discard" or "no")
            {
                return await rounds.DiscardAsync(watch, $"{author} said so", ct).ConfigureAwait(false);
            }

            var ask = $"A fix for PR !{watch.PullRequestId} ({watch.Pending.Commit[..Math.Min(7, watch.Pending.Commit.Length)]}) waits: **1** push · **2** discard.";
            await PostAsync(watch, ask, ct).ConfigureAwait(false);
            return ask;
        }

        var reply = watch.Active
            ? $"I'm watching PR !{watch.PullRequestId}: failed builds, conflicts and new comments. Say **unwatch** to stop."
            : $"I'm not watching PR !{watch.PullRequestId} anymore. `!watch {watch.PullRequestId}` starts again.";
        await PostAsync(watch, reply, ct).ConfigureAwait(false);
        return reply;
    }

    private async Task PostAsync(PrWatch watch, string text, CancellationToken ct) =>
        await providers.Resolve(watch.Provider).SendAsync(new ConversationRef(watch.Provider, watch.ThreadId, watch.SpaceId), new OutboundMessage(MessageKind.Info, text), ct).ConfigureAwait(false);

    private async Task<string> StopAsync(PrWatch watch, CancellationToken ct)
    {
        var stopped = await watches.StopAsync(watch.Id, ct).ConfigureAwait(false);
        var text = stopped ? $"🛑 Stopped watching PR !{watch.PullRequestId}." : $"PR !{watch.PullRequestId} wasn't being watched.";
        if (stopped)
        {
            await PostAsync(watch, text, ct).ConfigureAwait(false);
        }

        return text;
    }

    /// <summary>The PR and its repository: a link names both; a number needs <c>--repo</c> unless one repository is registered.</summary>
    private async Task<Result<(Domain.Repositories.Repository Repo, int Id)>> ResolveAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? repoName = null;
        string? reference = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--repo" && i + 1 < args.Count)
            {
                repoName = args[++i];
            }
            else
            {
                reference ??= args[i];
            }
        }

        if (reference is null || Reference().Match(reference) is not { Success: true } match)
        {
            return DomainError.Validation($"Which PR? {Usage}");
        }

        var all = await repositories.ListAsync(ct).ConfigureAwait(false);
        var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
        Domain.Repositories.Repository? repo = match.Groups["repo"].Success
            ? all.FirstOrDefault(r => string.Equals(r.AzureDevOps.Name, Uri.UnescapeDataString(match.Groups["repo"].Value), StringComparison.OrdinalIgnoreCase))
            : repoName is not null ? all.FirstOrDefault(r => string.Equals(r.Name.Value, repoName, StringComparison.OrdinalIgnoreCase))
            : all.Count == 1 ? all[0] : null;
        return repo is null
            ? DomainError.Validation(all.Count > 1 && repoName is null && !match.Groups["repo"].Success
                ? $"Several repositories are registered: paste the PR's link or add `--repo <name>` ({string.Join(", ", all.Select(r => r.Name.Value))})."
                : "That repository isn't registered (an admin adds it with `!repo add <clone url>`).")
            : (repo, id);
    }

    [GeneratedRegex(@"^(?:https?://\S+/_git/(?<repo>[^/\s]+)/pullrequest/|!)?(?<id>\d{1,9})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex Reference();
}
