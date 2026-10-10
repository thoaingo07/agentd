using System.Collections.Concurrent;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Reviews;

/// <summary>
/// Ask on a review page (docs/architect/review-sessions.md §2): a question about the selected code (or the whole change),
/// answered in the background from the review's read-only checkout. The page shows "thinking…" until the answer is in.
/// Each question starts a thread; follow-ups resume the thread's agent session, so the agent keeps the conversation.
/// </summary>
public sealed class ReviewSessionAsks(IReviewSessionStore store, IRepositoryRegistry repositories, IWorktreeManager worktrees, IBrainstormAgent agent) : IDisposable
{
    public const int MaxQuestion = 2000;

    /// <summary>The question a draft request shows as in its thread; its answer is the comment to post.</summary>
    public const string DraftQuestion = "✍️ Draft a review comment from this conversation.";

    /// <summary>Questions answered at once (each is a Claude Code process).</summary>
    public const int MaxConcurrent = 2;

    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly ConcurrentDictionary<long, Task> _answering = new();

    public async Task<Result<ReviewAsk>> AskAsync(long id, string? file, int? line, int? endLine, string question, string author, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > MaxQuestion)
        {
            return DomainError.Validation($"Ask a question (up to {MaxQuestion} characters).");
        }

        if (line is < 1 || (endLine is not null && (line is null || endLine < line)) || (line is not null && string.IsNullOrWhiteSpace(file)))
        {
            return DomainError.Validation("A question's lines start at 1, end at or after they start, and belong to a file.");
        }

        switch (await store.GetAsync(id, cancellationToken).ConfigureAwait(false))
        {
            case null:
                return DomainError.NotFound($"Review {id}");
            case { Status: ReviewSessionStatus.Closed }:
                return DomainError.Conflict($"Review {id} is closed.");
        }

        var askId = await store.AddAskAsync(id, string.IsNullOrWhiteSpace(file) ? null : file.Trim(), line, endLine, question.Trim(), author, null, cancellationToken).ConfigureAwait(false);
        var ask = (await store.ListAsksAsync(id, cancellationToken).ConfigureAwait(false)).Single(a => a.Id == askId);
        _ = Answer(id, ask);
        return ask;
    }

    /// <summary>Asks the thread's agent for the comment to post, from the conversation (a follow-up whose answer is the draft).</summary>
    public Task<Result<ReviewAsk>> DraftAsync(long id, long threadId, string author, CancellationToken cancellationToken) =>
        FollowUpAsync(id, threadId, DraftQuestion, author, cancellationToken);

    /// <summary>A follow-up in thread <paramref name="threadId"/> (its first question), once the last question there is answered.</summary>
    public async Task<Result<ReviewAsk>> FollowUpAsync(long id, long threadId, string question, string author, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > MaxQuestion)
        {
            return DomainError.Validation($"Ask a question (up to {MaxQuestion} characters).");
        }

        switch (await store.GetAsync(id, cancellationToken).ConfigureAwait(false))
        {
            case null:
                return DomainError.NotFound($"Review {id}");
            case { Status: ReviewSessionStatus.Closed }:
                return DomainError.Conflict($"Review {id} is closed.");
        }

        var askId = await store.AddAskAsync(id, null, null, null, question.Trim(), author, threadId, cancellationToken).ConfigureAwait(false);
        switch (askId)
        {
            case IReviewSessionStore.NoThread:
                return DomainError.NotFound($"Question {threadId} in review {id}");
            case IReviewSessionStore.ThreadBusy:
                return DomainError.Conflict("The last question in this thread is still being answered; ask again once it is.");
        }

        var ask = (await store.ListAsksAsync(id, cancellationToken).ConfigureAwait(false)).Single(a => a.Id == askId);
        _ = Answer(id, ask);
        return ask;
    }

    /// <summary>
    /// Answers in the background, once; a failure becomes the answer ("couldn't answer: …"), so the page never waits forever.
    /// The returned task completes when the answer is stored.
    /// </summary>
    public Task Answer(long sessionId, ReviewAsk ask) => _answering.GetOrAdd(ask.Id, _ => Task.Run(async () =>
    {
        try
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                if ((await store.ListAsksAsync(sessionId, CancellationToken.None).ConfigureAwait(false)).Any(a => a.Id == ask.Id && a.Answer is not null))
                {
                    return;   // answered already (a second call after the first finished)
                }

                await store.AnswerAsync(ask.Id, await AnswerTextAsync(sessionId, ask).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            _answering.TryRemove(ask.Id, out Task? _);
        }
    }));

    private async Task<string> AnswerTextAsync(long sessionId, ReviewAsk ask)
    {
        try
        {
            if (await store.GetAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is not { HeadCommit: { } head } session
                || RepositoryName.Create(session.Repository) is not { IsSuccess: true } name
                || await repositories.GetAsync(name.Value, CancellationToken.None).ConfigureAwait(false) is not { } repo)
            {
                return "⚠️ I couldn't answer: the review or its repository is gone.";
            }

            var worktree = await worktrees.CheckoutCommitAsync(repo, $"review-session-{sessionId}", head, CancellationToken.None).ConfigureAwait(false);
            var thread = (await store.ListAsksAsync(sessionId, CancellationToken.None).ConfigureAwait(false))
                .Where(a => a.Id == (ask.ThreadId ?? ask.Id) || a.ThreadId == (ask.ThreadId ?? ask.Id)).OrderBy(a => a.Id).ToList();
            var root = thread.FirstOrDefault(a => a.ThreadId is null) ?? ask;
            BrainstormTurn Turn(Guid agentSession, bool resume, string prompt) =>
                new(root.Id, worktree, agentSession, resume, prompt, session.Model, session.Effort, ThreadTurnKind.ReviewAsk);

            BrainstormReply reply;
            if (ask.ThreadId is null)
            {
                reply = await agent.RunAsync(Turn(root.AgentSession ?? Guid.NewGuid(), false, Prompt(session, ask)), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                reply = root.AgentSession is { } previous
                    ? await agent.RunAsync(Turn(previous, true, FollowUp(root, ask)), CancellationToken.None).ConfigureAwait(false)
                    : new BrainstormReply(null, null, "no session");
                if (reply is { Text: null, UsageLimitedUntil: null })
                {
                    // The session can't be resumed (it never started, or it's gone): start over with the conversation so far.
                    var fresh = Guid.NewGuid();
                    reply = await agent.RunAsync(Turn(fresh, false, Prompt(session, root, thread.Where(a => a.Id < ask.Id).ToList(), ask)), CancellationToken.None).ConfigureAwait(false);
                    if (reply.Text is not null)
                    {
                        await store.SetAskSessionAsync(root.Id, fresh, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }

            return reply switch
            {
                { UsageLimitedUntil: { } until } => $"⚠️ I couldn't answer: the Claude usage limit is reached until {until:HH:mm} UTC.",
                { Text: { Length: > 0 } text } => text.Trim(),
                _ => $"⚠️ I couldn't answer ({reply.Error ?? "no reply"}). Ask again.",
            };
        }
#pragma warning disable CA1031 // any failure is shown as the answer
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return $"⚠️ I couldn't answer: {ex.Message}";
        }
    }

    /// <summary>A follow-up whose session is gone: the first question's prompt, the conversation so far, then the follow-up.</summary>
    internal static string Prompt(ReviewSession s, ReviewAsk root, IReadOnlyList<ReviewAsk> earlier, ReviewAsk followUp)
    {
        var sb = new System.Text.StringBuilder(Prompt(s, root));
        foreach (var a in earlier)
        {
            if (a.Id != root.Id)
            {
                sb.Append("\n\n").Append(a.Author).Append(" followed up:\n\n").Append(a.Question);
            }

            sb.Append("\n\nYou answered:\n\n").Append(a.Answer ?? "(no answer)");
        }

        return sb.Append("\n\n").Append(FollowUp(root, followUp)).ToString();
    }

    /// <summary>What the agent is told for a follow-up: the question, or for a draft, how to write the comment.</summary>
    internal static string FollowUp(ReviewAsk root, ReviewAsk ask) => ask.Question == DraftQuestion
        ? $"{ask.Author} asks you to draft the comment they'll post on the pull request at {Where(root)}, from this conversation. " +
            "Reply with only the comment, in Markdown: short, specific and polite, addressed to the change's author, with no preamble."
        : $"{ask.Author} follows up:\n\n{ask.Question}";

    private static string Where(ReviewAsk ask) =>
        ask.File is null ? "the whole change" : ask.Line is null ? ask.File : ask.EndLine is { } end && end != ask.Line ? $"{ask.File}:{ask.Line}-{end}" : $"{ask.File}:{ask.Line}";

    internal static string Prompt(ReviewSession s, ReviewAsk ask)
    {
        var where = Where(ask);
        return $"On agentd's review page for {s.HeadRef} → {s.BaseRef} in {s.Repository} (the change is `git diff {s.BaseCommit} {s.HeadCommit}`; your checkout is {s.HeadCommit}), " +
            $"{ask.Author} selected {where} and asks:\n\n{ask.Question}";
    }

    public void Dispose() => _slots.Dispose();
}
