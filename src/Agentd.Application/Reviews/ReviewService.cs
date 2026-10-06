using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace Agentd.Application.Reviews;

/// <summary>
/// <c>!review</c> (Phase 7a): opens a thread, reviews the PR head read-only, and keeps the conversation going. Findings are
/// posted to the PR only when a person chooses (all, some, or none); agentd never votes or approves. One turn at a time
/// per review; at most <see cref="MaxConcurrentReviews"/> reviews think at once.
/// </summary>
public sealed partial class ReviewService(
    IReviewStore reviews,
    IRepositoryRegistry repositories,
    IPullRequestService pullRequests,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    IMessagingProviderRegistry providers,
    ILogger<ReviewService> logger,
    IWorkItemSource? workItems = null) : IDisposable
{
    public const int MaxConcurrentReviews = 2;

    /// <summary>How much context goes into the first prompt (the agent reads the rest itself).</summary>
    public const int MaxLinkedWorkItems = 3;
    public const int MaxOpenThreads = 20;

    private readonly ConcurrentDictionary<long, Turns> _queues = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentReviews, MaxConcurrentReviews);

    public void Dispose() => _slots.Dispose();

    /// <summary><c>review &lt;PR url or id&gt; [instructions…] [--repo r] [--focus f] [--model m] [--effort e]</c>; returns the reply for the channel.</summary>
    public async Task<Result<string>> StartAsync(ProviderKey provider, string author, IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        // The first word is the PR; --repo/--focus/--model/--effort go anywhere; everything else is instructions for the reviewer.
        var (rest, repoName, model, effort, problem) = BrainstormSettings.Parse(Unfocus(args, out var focus));
        if (problem is not null)
        {
            return DomainError.Validation(problem);
        }

        var words = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var instructions = words.Length > 1 ? words[1] : null;
        if (words.Length == 0 || PrReference().Match(words[0]) is not { Success: true } pr)
        {
            return DomainError.Validation("Which PR? Use `!review <PR url or id> [instructions] [--repo r] [--focus security,tests] [--model m] [--effort e]`.");
        }

        var repo = await ResolveRepositoryAsync(pr, repoName, ct).ConfigureAwait(false);
        if (!repo.IsSuccess)
        {
            return repo.Error;
        }

        var id = int.Parse(pr.Groups["id"].Value, CultureInfo.InvariantCulture);
        var details = await pullRequests.GetAsync(repo.Value, id, ct).ConfigureAwait(false);
        if (details is null)
        {
            return DomainError.NotFound($"PR !{id} in `{repo.Value.Name}`");
        }

        if (details.Status != PullRequestStatus.Active)
        {
            return DomainError.Validation($"PR !{id} is {details.Status.ToString().ToLowerInvariant()}, so there's nothing to review.");
        }

        var opening = new OutboundMessage(MessageKind.Info,
            $"🔍 **Review of PR !{id}** requested by {author}: {details.Title}\n`{details.SourceBranch}` → `{details.TargetBranch}` · by {details.Author} · {details.Url}\n\n" +
            (instructions is null ? string.Empty : $"Your instructions: {instructions}\n\n") +
            "I'm reading the change and will post my findings here. **Nothing goes to the PR until you choose.** Ask me anything in this thread.");
        var title = $"PR !{id}: {details.Title}";
        var thread = await providers.Resolve(provider).OpenConversationAsync(
            new ConversationSpec(default, default, title, repo.Value.Name, opening, $"🔍 Review: {IdeaService.Title(title)}"), ct).ConfigureAwait(false);
        var reviewId = await reviews.InsertAsync(repo.Value.Name.Value, id, details.Title, author, provider, thread.ExternalConversationId, thread.ExternalSpaceId, ct).ConfigureAwait(false);
        var review = (await reviews.GetAsync(reviewId, ct).ConfigureAwait(false))! with { HeadCommit = details.SourceCommit, Focus = focus, Model = model, Effort = effort };
        await reviews.SaveAsync(review, ct).ConfigureAwait(false);

        var prompt = string.Create(CultureInfo.InvariantCulture,
            $"Review pull request !{id} \"{details.Title}\" by {details.Author} in {repo.Value.Name}: `{details.SourceBranch}` → `{details.TargetBranch}` " +
            $"(head {details.SourceCommit}; the target is origin/{details.TargetBranch}). Requested by {author}.")
            + (focus is null ? string.Empty : $" Focus on: {focus}.")
            + (instructions is null ? string.Empty : $"\n\nInstructions from {author}: {instructions}")
            + (string.IsNullOrWhiteSpace(details.Description) ? string.Empty : $"\n\nPR description:\n{Clip(details.Description, 3000)}")
            + await WorkItemContextAsync(details, ct).ConfigureAwait(false)
            + await OpenThreadsContextAsync(repo.Value, id, ct).ConfigureAwait(false);
        await reviews.AddMessageAsync(reviewId, "in", author, $"!review {string.Join(' ', args)}", ct).ConfigureAwait(false);
        Enqueue(reviewId, prompt);
        return $"🔍 Started a review thread for PR !{id} (review #{reviewId}).";
    }

    /// <summary>The linked work items' acceptance criteria and description: what the PR is supposed to do.</summary>
    private async Task<string> WorkItemContextAsync(PullRequestDetails pr, CancellationToken ct)
    {
        if (workItems is null || pr.WorkItems is not { Count: > 0 } ids)
        {
            return "\n\nLinked work items: none (judge the PR by its title and description).";
        }

        var parts = new List<string>();
        foreach (var id in ids.Take(MaxLinkedWorkItems))
        {
            try
            {
                if (await workItems.GetAsync(id, ct).ConfigureAwait(false) is { } item)
                {
                    parts.Add($"WI-{item.Id} \"{item.Title}\" ({item.State})"
                        + (string.IsNullOrWhiteSpace(item.AcceptanceCriteria) ? "\nAcceptance criteria: none written." : $"\nAcceptance criteria:\n{Clip(item.AcceptanceCriteria, 2000)}")
                        + (string.IsNullOrWhiteSpace(item.Description) ? string.Empty : $"\nDescription:\n{Clip(item.Description, 1500)}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                parts.Add($"WI-{id}: couldn't be read ({ex.Message}).");
            }
        }

        return "\n\nLinked work items:\n" + string.Join("\n\n", parts);
    }

    /// <summary>What reviewers already said (open human threads), so the agent doesn't repeat it.</summary>
    private async Task<string> OpenThreadsContextAsync(Repository repo, int id, CancellationToken ct)
    {
        try
        {
            var open = (await pullRequests.ListCommentsAsync(repo, id, ct).ConfigureAwait(false)).Where(c => c.IsOpen).ToList();
            if (open.Count == 0)
            {
                return "\n\nOpen PR comment threads: none.";
            }

            var lines = open.Take(MaxOpenThreads).Select(c =>
                $"- {c.Author}{(c.FilePath is null ? string.Empty : $" on {c.FilePath.TrimStart('/')}{(c.Line is { } l ? $":{l}" : string.Empty)}")}: {Clip(c.Content, 300).ReplaceLineEndings(" ")}");
            return "\n\nOpen PR comment threads (already raised, don't repeat them):\n" + string.Join('\n', lines)
                + (open.Count > MaxOpenThreads ? $"\n- … and {open.Count - MaxOpenThreads} more" : string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"\n\nOpen PR comment threads: couldn't be read ({ex.Message}).";
        }
    }

    private static string Clip(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..(max - 1)] + "…";
    }

    /// <summary><c>!model</c> / <c>!effort</c> in a review's thread: used from the next reply.</summary>
    public async Task<string> ChangeSettingsAsync(Review review, string? model, string? effort, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (model is not null && !BrainstormSettings.IsModel(model))
        {
            return $"`{model}` isn't a model name (use fable, opus, sonnet or a full model name).";
        }

        if (effort is not null && !BrainstormSettings.IsEffort(effort))
        {
            return $"`{effort}` isn't an effort level ({string.Join(", ", BrainstormSettings.Efforts)}).";
        }

        var updated = review with { Model = model ?? review.Model, Effort = effort?.ToLowerInvariant() ?? review.Effort };
        await reviews.SaveAsync(updated, ct).ConfigureAwait(false);
        return $"⚙️ From my next reply: model **{updated.Model ?? "default"}**, effort **{updated.Effort ?? "default"}**.";
    }

    public const string LabelPost = "📤 Post to the PR";
    public const string LabelKeep = "💬 Keep in chat";
    public const string LabelDiscard = "🗑 Discard";
    public const string LabelDelete = "🗑 Delete thread";
    public const string LabelArchive = "📦 Keep (archive)";

    /// <summary>A message in a review's thread. Returns false when the review no longer takes messages.</summary>
    public async Task<bool> HandleMessageAsync(Review review, string author, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(text);
        var answer = text.Trim().TrimEnd('.', '!');
        if (review.Status == ReviewStatus.Closed)
        {
            return false;
        }

        if (review.Status is ReviewStatus.Posted or ReviewStatus.Kept or ReviewStatus.Discarded && CloseOutAnswer(answer) is { } delete)
        {
            await CloseOutAsync(review, delete, author, ct).ConfigureAwait(false);
            return true;
        }

        if (review.Status == ReviewStatus.Discarded)
        {
            await PostAsync(review, new OutboundMessage(MessageKind.Info, "This review was discarded. Start a new one with `!review <PR>`."), ct).ConfigureAwait(false);
            return false;
        }

        if (review.Status == ReviewStatus.Reviewed && review.Result is { } result && ReviewChoice.Parse(answer, result.Findings.Count) is { } choice)
        {
            await reviews.AddMessageAsync(review.Id, "in", author, answer, ct).ConfigureAwait(false);
            await ChooseAsync(review, result, choice, author, ct).ConfigureAwait(false);
            return true;
        }

        await reviews.AddMessageAsync(review.Id, "in", author, text, ct).ConfigureAwait(false);
        if (!Enqueue(review.Id, $"{author}: {text}"))
        {
            await PostAsync(review, new OutboundMessage(MessageKind.Info, "💭 Still thinking about the last message; I'll answer this right after."), ct).ConfigureAwait(false);
        }

        return true;
    }

    private async Task ChooseAsync(Review review, ReviewResult result, ReviewChoice choice, string author, CancellationToken ct)
    {
        switch (choice.Kind)
        {
            case ReviewChoiceKind.Drop:
                var kept = result with { Findings = [.. result.Findings.Where((_, i) => !choice.Numbers.Contains(i + 1))] };
                await reviews.SaveAsync(review with { Result = kept }, ct).ConfigureAwait(false);
                await PostAsync(review, Choices(kept, $"🗑 Dropped {string.Join(", ", choice.Numbers.Select(n => $"#{n}"))} ({author}). Renumbered:\n\n"), ct).ConfigureAwait(false);
                return;
            case ReviewChoiceKind.Keep:
                await reviews.SaveAsync(review with { Status = ReviewStatus.Kept }, ct).ConfigureAwait(false);
                await PostAsync(review, new OutboundMessage(MessageKind.Info, $"💬 Kept in chat ({author}); nothing was posted to the PR."), ct).ConfigureAwait(false);
                await AskCloseOutAsync(review, ct).ConfigureAwait(false);
                return;
            case ReviewChoiceKind.Discard:
                await reviews.SaveAsync(review with { Status = ReviewStatus.Discarded }, ct).ConfigureAwait(false);
                await PostAsync(review, new OutboundMessage(MessageKind.Info, $"🗑 Discarded by {author}; nothing was posted."), ct).ConfigureAwait(false);
                await AskCloseOutAsync(review, ct).ConfigureAwait(false);
                return;
            default:
                var selected = choice.Numbers.Count == 0 ? result.Findings : [.. choice.Numbers.Select(n => result.Findings[n - 1])];
                await PublishAsync(review, result, selected, author, ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>One PR thread per finding (at its file and line), then a summary thread. Never a vote.</summary>
    private async Task PublishAsync(Review review, ReviewResult result, IReadOnlyList<ReviewFinding> selected, string author, CancellationToken ct)
    {
        if (await repositories.GetAsync(RepositoryName.From(review.Repository), ct).ConfigureAwait(false) is not { } repo)
        {
            await PostAsync(review, new OutboundMessage(MessageKind.Info, $"I can't post: repository `{review.Repository}` is no longer registered."), ct).ConfigureAwait(false);
            return;
        }

        var posted = new List<int>(review.PostedThreads);
        string? failure = null;
        foreach (var f in selected)
        {
            var text = $"**{ReviewFindings.Icon(f.Severity)}: {f.Title.Trim()}**" + (string.IsNullOrWhiteSpace(f.Detail) ? string.Empty : $"\n\n{f.Detail.Trim()}")
                + (string.IsNullOrWhiteSpace(f.Suggestion) ? string.Empty : $"\n\n💡 {f.Suggestion.Trim()}");
            try
            {
                posted.Add(await pullRequests.CreateThreadAsync(repo, review.PullRequestId, text, f.File, f.Line, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = $"\"{f.Title}\": {ex.Message}";
                break;
            }
        }

        var count = posted.Count - review.PostedThreads.Count;
        if (failure is null)
        {
            try
            {
                posted.Add(await pullRequests.CreateThreadAsync(repo, review.PullRequestId,
                    $"**Review** requested by {author} in chat: {result.Summary.Trim()}\n\n{count} finding(s) posted as separate threads.", null, null, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = $"the summary: {ex.Message}";
            }
        }

        await reviews.SaveAsync(review with { Status = count > 0 || failure is null ? ReviewStatus.Posted : review.Status, PostedThreads = posted }, ct).ConfigureAwait(false);
        var link = (await pullRequests.GetAsync(repo, review.PullRequestId, ct).ConfigureAwait(false))?.Url;
        await PostAsync(review, new OutboundMessage(MessageKind.Result, failure is null
            ? $"📤 **Posted {count} finding(s) and a summary to PR !{review.PullRequestId}** ({author}){(link is null ? string.Empty : $": {link}")}. No vote was cast."
            : $"⚠️ Posted {count} finding(s), then stopped at {failure}. Fix the cause and choose again, or post the rest by hand."), ct).ConfigureAwait(false);
        if (failure is null)
        {
            await AskCloseOutAsync(review, ct).ConfigureAwait(false);
        }
    }

    private bool Enqueue(long reviewId, string prompt)
    {
        var queue = _queues.GetOrAdd(reviewId, _ => new Turns());
        lock (queue)
        {
            queue.Prompts.Add(prompt);
            if (queue.Running)
            {
                return false;
            }

            queue.Running = true;
        }

        _ = Task.Run(() => DrainAsync(reviewId, queue));
        return true;
    }

    private async Task DrainAsync(long reviewId, Turns queue)
    {
        while (true)
        {
            string prompt;
            lock (queue)
            {
                if (queue.Prompts.Count == 0)
                {
                    queue.Running = false;
                    return;
                }

                prompt = string.Join("\n\n", queue.Prompts);
                queue.Prompts.Clear();
            }

            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                await TurnAsync(reviewId, prompt, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // one failed turn must not stop the review's queue
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogTurnFailed(logger, ex, reviewId);
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    private async Task TurnAsync(long reviewId, string prompt, CancellationToken ct)
    {
        if (await reviews.GetAsync(reviewId, ct).ConfigureAwait(false) is not { } review)
        {
            return;
        }

        var repo = await repositories.GetAsync(RepositoryName.From(review.Repository), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository '{review.Repository}' is no longer registered.");
        // New pushes since the last turn: move the checkout to the new head and tell the agent.
        var head = (await pullRequests.GetAsync(repo, review.PullRequestId, ct).ConfigureAwait(false))?.SourceCommit ?? review.HeadCommit
            ?? throw new InvalidOperationException($"PR !{review.PullRequestId} has no head commit.");
        if (review.HeadCommit is { } before && before != head)
        {
            prompt = $"(agentd) The PR has new commits since your last look: the head is now {head} (was {before}).\n\n{prompt}";
        }

        var worktree = await worktrees.CheckoutCommitAsync(repo, $"review-{review.Id}", head, ct).ConfigureAwait(false);
        var session = review.Session ?? Guid.NewGuid();
        var reply = await agent.RunAsync(new BrainstormTurn(review.Id, worktree, session, review.Session is not null, prompt, review.Model, review.Effort, ThreadTurnKind.Review), ct).ConfigureAwait(false);
        review = review with { Session = session, Worktree = worktree, HeadCommit = head };

        if (reply.UsageLimitedUntil is { } until || reply.Text is null)
        {
            var why = reply.UsageLimitedUntil is { } u
                ? $"⏸ The Claude usage limit is reached until {u:HH:mm} UTC. Send your message again after that."
                : $"⚠️ I couldn't finish that reply ({reply.Error ?? "no answer"}). Send it again to retry.";
            await reviews.SaveAsync(review, ct).ConfigureAwait(false);
            await PostAsync(review, new OutboundMessage(MessageKind.Info, why), ct).ConfigureAwait(false);
            return;
        }

        var (text, result, problem) = ReviewFindings.Extract(reply.Text);
        if (result is not null)
        {
            review = review with { Result = result, Status = review.Status is ReviewStatus.Reviewing or ReviewStatus.Reviewed ? ReviewStatus.Reviewed : review.Status };
        }

        await reviews.SaveAsync(review, ct).ConfigureAwait(false);
        await reviews.AddMessageAsync(review.Id, "out", "agent", reply.Text, ct).ConfigureAwait(false);
        if (text.Length > 0)
        {
            await PostAsync(review, new OutboundMessage(MessageKind.Info, text), ct).ConfigureAwait(false);
        }

        if (result is not null && review.Status == ReviewStatus.Reviewed)
        {
            await PostAsync(review, Choices(result, string.Empty), ct).ConfigureAwait(false);
        }
        else if (problem is not null)
        {
            Enqueue(review.Id, $"(agentd) Your review-findings block couldn't be read: {problem}. Send the corrected block.");
        }
    }

    private static OutboundMessage Choices(ReviewResult result, string prefix) => result.Findings.Count == 0
        ? new(MessageKind.Question, prefix + ReviewFindings.Render(result) + "\n\n**1** post the summary to the PR · **2** keep in chat · **3** discard. Or ask me anything.",
            [new MessageOption("review-post", LabelPost), new MessageOption("review-keep", LabelKeep), new MessageOption("review-discard", LabelDiscard)])
        : new(MessageKind.Question, prefix + ReviewFindings.Render(result) +
            "\n\n**1** post all to the PR · **2** keep in chat · **3** discard · or `post 1,3` / `drop 2`. Ask me about any finding first if you like.",
            [new MessageOption("review-post", LabelPost), new MessageOption("review-keep", LabelKeep), new MessageOption("review-discard", LabelDiscard)]);

    private static bool? CloseOutAnswer(string answer) =>
        Is(answer, "1", "delete", LabelDelete) ? true : Is(answer, "2", "keep", "archive", LabelArchive) ? false : null;

    private static bool Is(string answer, params string[] options) => options.Any(o => string.Equals(answer, o, StringComparison.OrdinalIgnoreCase));

    private Task AskCloseOutAsync(Review review, CancellationToken ct) =>
        PostAsync(review, new OutboundMessage(MessageKind.Question, "🧹 **Done.** Delete this thread? **1** delete · **2** keep it (archived). The conversation stays in agentd either way.",
            [new MessageOption("review-delete", LabelDelete), new MessageOption("review-archive", LabelArchive)]), ct);

    private async Task CloseOutAsync(Review review, bool delete, string author, CancellationToken ct)
    {
        await reviews.SaveAsync(review with { Status = ReviewStatus.Closed }, ct).ConfigureAwait(false);
        var provider = providers.Resolve(review.Provider);
        var thread = new ConversationRef(review.Provider, review.ThreadId, review.SpaceId);
        try
        {
            if (delete)
            {
                await provider.DeleteConversationAsync(thread, ct).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostFailed(logger, ex, review.Id);
        }

        try
        {
            await provider.CloseConversationAsync(thread, $"Closed by {author}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostFailed(logger, ex, review.Id);
        }
    }

    /// <summary>A PR URL names its repository; a bare id uses <c>--repo</c>, or the only registered repository.</summary>
    private async Task<Result<Repository>> ResolveRepositoryAsync(Match pr, string? name, CancellationToken ct)
    {
        var all = await repositories.ListAsync(ct).ConfigureAwait(false);
        if (pr.Groups["repo"].Success)
        {
            var (org, project, repoName) = (Uri.UnescapeDataString(pr.Groups["org"].Value), Uri.UnescapeDataString(pr.Groups["project"].Value), Uri.UnescapeDataString(pr.Groups["repo"].Value));
            return all.FirstOrDefault(r => string.Equals(r.AzureDevOps.Organization, org, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.AzureDevOps.Project, project, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.AzureDevOps.Name, repoName, StringComparison.OrdinalIgnoreCase)) is { } match
                ? match
                : DomainError.NotFound($"A registered repository for {org}/{project}/{repoName} (add it with `!repo add <clone url>`)");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return all.FirstOrDefault(r => string.Equals(r.Name.Value, name, StringComparison.OrdinalIgnoreCase)) is { } named ? named : DomainError.NotFound($"Repository '{name}'");
        }

        return all.Count switch
        {
            1 => all[0],
            0 => DomainError.Validation("No repository is registered yet: an admin can add one with `!repo add <clone url>`."),
            _ => DomainError.Validation($"Several repositories are registered: paste the PR's URL, or add `--repo <name>` ({string.Join(", ", all.Select(r => r.Name.Value))})."),
        };
    }

    /// <summary>Takes <c>--focus x</c> off the arguments.</summary>
    private static List<string> Unfocus(IReadOnlyList<string> args, out string? focus)
    {
        focus = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("--focus", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                focus = args[++i].Trim();
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        return rest;
    }

    private async Task PostAsync(Review review, OutboundMessage message, CancellationToken ct)
    {
        try
        {
            var provider = providers.Resolve(review.Provider);
            foreach (var part in MessageChunker.Prepare(message, provider.Capabilities))
            {
                await provider.SendAsync(new ConversationRef(review.Provider, review.ThreadId, review.SpaceId), part, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostFailed(logger, ex, review.Id);
        }
    }

    /// <summary>A PR URL (<c>https://dev.azure.com/org/project/_git/repo/pullrequest/123</c>), <c>!123</c> or <c>123</c>.</summary>
    [GeneratedRegex(@"^(?:https://dev\.azure\.com/(?<org>[^/\s]+)/(?<project>[^/\s]+)/_git/(?<repo>[^/\s]+)/pullrequest/|!?)(?<id>\d{1,9})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex PrReference();

    private sealed class Turns
    {
        public List<string> Prompts { get; } = [];

        public bool Running { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Review {ReviewId}: the review turn failed")]
    private static partial void LogTurnFailed(ILogger logger, Exception exception, long reviewId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review {ReviewId}: posting to the thread failed")]
    private static partial void LogPostFailed(ILogger logger, Exception exception, long reviewId);
}

public enum ReviewChoiceKind
{
    Post,
    Keep,
    Discard,
    Drop,
}

/// <summary>An answer to the findings: <c>1</c>/<c>post</c> (all) or <c>post 1,3</c>; <c>2</c>/<c>keep</c>; <c>3</c>/<c>discard</c>; <c>drop 2,4</c>.</summary>
public sealed partial record ReviewChoice(ReviewChoiceKind Kind, IReadOnlyList<int> Numbers)
{
    /// <summary>The choice, or null if the text isn't one (then it's a question for the agent). Numbers must exist (1..<paramref name="count"/>).</summary>
    public static ReviewChoice? Parse(string answer, int count)
    {
        var text = (answer ?? string.Empty).Trim().ToLowerInvariant();
        switch (text)
        {
            case "1" or "post" or "post all" or "📤 post to the pr":
                return new(ReviewChoiceKind.Post, []);
            case "2" or "keep" or "keep in chat" or "💬 keep in chat":
                return new(ReviewChoiceKind.Keep, []);
            case "3" or "discard" or "🗑 discard":
                return new(ReviewChoiceKind.Discard, []);
        }

        var match = Selection().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var numbers = match.Groups["n"].Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(n => int.Parse(n, CultureInfo.InvariantCulture)).Distinct().Order().ToList();
        return numbers.Count == 0 || numbers.Any(n => n < 1 || n > count)
            ? null
            : new(match.Groups["verb"].Value == "drop" ? ReviewChoiceKind.Drop : ReviewChoiceKind.Post, numbers);
    }

    [GeneratedRegex(@"^(?<verb>post|drop)\s+#?(?<n>\d{1,2}(?:\s*,\s*#?\d{1,2}|\s+#?\d{1,2})*)$")]
    private static partial Regex Selection();
}
