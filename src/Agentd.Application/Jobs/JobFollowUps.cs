using System.Collections.Concurrent;
using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace Agentd.Application.Jobs;

/// <summary>
/// Talk only, after the PR is merged (decided 2026-10-06): a message in a finished job's thread resumes the job's own
/// Claude session read-only, so the agent answers about the work it did, with no edits, commits, pushes or new PRs.
/// For changes it points to <c>!run</c>. One turn at a time per job; messages that arrive meanwhile wait for it.
/// </summary>
public sealed partial class JobFollowUps(
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    IOutbox outbox,
    ILogger<JobFollowUps> logger) : IDisposable
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _turns = new();

    /// <summary>Whether <paramref name="job"/> answers follow-ups: its PR is merged (or it's done with a PR) and it has a session.</summary>
    public static bool Accepts(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.PullRequest is not null && job.Session is not null && job.State is JobState.Done or JobState.InReview;
    }

    /// <summary>Queues the question; the answer is posted to the job's thread when the turn ends.</summary>
    public void Ask(Job job, string author, string text)
    {
        ArgumentNullException.ThrowIfNull(job);
        _ = Task.Run(() => TurnAsync(job, author, text));
    }

    public void Dispose()
    {
        foreach (var gate in _turns.Values)
        {
            gate.Dispose();
        }
    }

    private async Task TurnAsync(Job job, string author, string text)
    {
        var gate = _turns.GetOrAdd(job.Id.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var repo = await repositories.GetAsync(job.Repository, CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Repository '{job.Repository}' is no longer registered.");
            // The session belongs to the job's folder: reuse it, or put a read-only checkout of the base branch (which now
            // has the merged work) back at the same path.
            var worktree = job.Worktree is { } w && Directory.Exists(w.Value)
                ? w.Value
                : await worktrees.CheckoutDetachedAsync(repo, $"wi-{job.WorkItemId}", CancellationToken.None).ConfigureAwait(false);
            var prompt = $"(agentd) The pull request {job.PullRequest} is merged and this job is finished. {author} writes in the thread:\n\n{text}";
            var reply = await agent.RunAsync(new BrainstormTurn(job.Id.Value, worktree, job.Session!.Value.Value, Resume: true, prompt, Kind: ThreadTurnKind.FollowUp),
                CancellationToken.None).ConfigureAwait(false);
            var answer = reply.Text
                ?? (reply.UsageLimitedUntil is { } until
                    ? $"⏸ The Claude usage limit is reached until {until:HH:mm} UTC. Ask again after that."
                    : $"⚠️ I couldn't answer that ({reply.Error ?? "no answer"}). Send it again to retry.");
            await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, answer), CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a failed follow-up must not take the dispatcher down
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex, job.Id.Value);
            await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, "⚠️ I couldn't answer that just now. Send it again to retry."), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId}: the follow-up turn failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, long jobId);
}
