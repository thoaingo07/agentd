using System.Collections.Concurrent;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;

namespace Agentd.Application.Setup;

/// <summary>Something a restart would cut off: a job mid-step, or an agent's thread turn.</summary>
/// <param name="Source">job, idea, chat, review or incident.</param>
/// <param name="Id">The job, idea, chat, review or watch.</param>
/// <param name="Label">What it is doing, for people.</param>
public sealed record RunningWork(string Source, long Id, string Label);

/// <summary>
/// The agent thread turns running now (ideas, chats, reviews, a job's follow-ups), kept in memory: every
/// <see cref="IBrainstormAgent"/> run goes through <see cref="TrackedBrainstormAgent"/>.
/// </summary>
public sealed class ThreadTurns
{
    private readonly ConcurrentDictionary<long, (ThreadTurnKind Kind, long Id)> _running = new();
    private long _next;

    public IReadOnlyList<(ThreadTurnKind Kind, long Id)> Running => [.. _running.OrderBy(t => t.Key).Select(t => t.Value)];

    /// <summary>Marks a turn running until the result is disposed.</summary>
    public IDisposable Begin(ThreadTurnKind kind, long id)
    {
        var key = Interlocked.Increment(ref _next);
        _running[key] = (kind, id);
        return new Turn(this, key);
    }

    private sealed class Turn(ThreadTurns turns, long key) : IDisposable
    {
        public void Dispose() => turns._running.TryRemove(key, out _);
    }
}

/// <summary>Any runner's thread turns, counted in <see cref="ThreadTurns"/> while they run.</summary>
public sealed class TrackedBrainstormAgent(IBrainstormAgent inner, ThreadTurns turns) : IBrainstormAgent
{
    public async Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        using (turns.Begin(turn.Kind, turn.IdeaId))
        {
            return await inner.RunAsync(turn, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// What Settings → Restart now would interrupt: jobs preparing, running or publishing, and agent thread turns in progress.
/// The restart waits for an Admin's "restart anyway" while there are any (docs/architect/deployment.md).
/// </summary>
public sealed class RestartCheck(IJobRepository jobs, ThreadTurns turns)
{
    /// <summary>A job in these states has an agent or a git push under way; waiting for a person or a review doesn't count.</summary>
    public static readonly JobState[] Busy = [JobState.Preparing, JobState.Running, JobState.Publishing];

    public async Task<IReadOnlyList<RunningWork>> RunningAsync(CancellationToken cancellationToken)
    {
        var running = (await jobs.ListByStateAsync(Busy, cancellationToken).ConfigureAwait(false))
            .OrderBy(j => j.Id.Value)
            .Select(j => new RunningWork("job", j.Id.Value, $"Job {j.Id.Value} (WI-{j.WorkItemId.Value}) is {j.State.ToString().ToLowerInvariant()}"))
            .ToList();
        running.AddRange(turns.Running.Select(t => Describe(t.Kind, t.Id)));
        return running;
    }

    private static RunningWork Describe(ThreadTurnKind kind, long id) => kind switch
    {
        ThreadTurnKind.Brainstorm => new("idea", id, $"Idea {id}: the agent is answering"),
        ThreadTurnKind.Chat => new("chat", id, $"Chat {id}: the agent is answering"),
        ThreadTurnKind.Incident => new("incident", id, $"Incident {id}: the agent is investigating"),
        ThreadTurnKind.FollowUp => new("job", id, $"Job {id}: the agent is answering a follow-up"),
        ThreadTurnKind.Review => new("review", id, $"Chat review {id}: the agent is reviewing"),
        ThreadTurnKind.ReviewSession => new("review", id, $"Review {id}: the agent is reviewing"),
        ThreadTurnKind.ReviewAsk => new("review", id, $"Review {id}: the agent is answering a question"),
        ThreadTurnKind.ReviewFix => new("review", id, $"Fix {id} (a review or a watched PR): the agent is changing code"),
        _ => new("agent", id, $"{kind} {id}: the agent is working"),
    };
}
