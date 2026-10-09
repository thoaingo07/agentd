using System.Collections.Concurrent;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Reviews;

/// <summary>
/// Ask on a review page (docs/architect/review-sessions.md §2): a question about the selected code (or the whole change),
/// answered in the background from the review's read-only checkout. The page shows "thinking…" until the answer is in.
/// </summary>
public sealed class ReviewSessionAsks(IReviewSessionStore store, IRepositoryRegistry repositories, IWorktreeManager worktrees, IBrainstormAgent agent) : IDisposable
{
    public const int MaxQuestion = 2000;

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

        var askId = await store.AddAskAsync(id, string.IsNullOrWhiteSpace(file) ? null : file.Trim(), line, endLine, question.Trim(), author, cancellationToken).ConfigureAwait(false);
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
            var reply = await agent.RunAsync(new BrainstormTurn(ask.Id, worktree, Guid.NewGuid(), false, Prompt(session, ask), session.Model, session.Effort, ThreadTurnKind.ReviewAsk),
                CancellationToken.None).ConfigureAwait(false);
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

    internal static string Prompt(ReviewSession s, ReviewAsk ask)
    {
        var where = ask.File is null ? "the whole change" : ask.Line is null ? ask.File : ask.EndLine is { } end && end != ask.Line ? $"{ask.File}:{ask.Line}-{end}" : $"{ask.File}:{ask.Line}";
        return $"On agentd's review page for {s.HeadRef} → {s.BaseRef} in {s.Repository} (the change is `git diff {s.BaseCommit} {s.HeadCommit}`; your checkout is {s.HeadCommit}), " +
            $"{ask.Author} selected {where} and asks:\n\n{ask.Question}";
    }

    public void Dispose() => _slots.Dispose();
}
