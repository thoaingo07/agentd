using System.Collections.Concurrent;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>
/// Puts a turn on its profile's session. The default profile is the job's own session (<c>jobs.claude_session_id</c>);
/// another profile (DeepSeek, …) has its own (<see cref="IJobSessions"/>), started with a handoff the first time and
/// resumed after that. A session that missed turns run elsewhere gets a short catch-up note.
/// </summary>
public sealed class ProfileSessions(IJobSessions sessions, IJobPlans plans, IJobRepository jobs)
{
    /// <summary>The <c>job_sessions</c> row of the job's own session (the Claude subscription, <c>Claude:*</c>).</summary>
    public const string DefaultProfile = "default";

    // The profile of each job's last turn (in memory: after a restart, the next turn simply gets no catch-up note).
    private readonly ConcurrentDictionary<long, string> _last = new();

    public async Task<AgentRunRequest> ApplyAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile ?? DefaultProfile;
        var switched = _last.TryGetValue(request.JobId.Value, out var last) && !string.Equals(last, profile, StringComparison.OrdinalIgnoreCase);
        _last[request.JobId.Value] = profile;

        // The default profile's session is the job's own; another profile's is created here on first use.
        var proposed = request.Profile is null ? request.Session.Value : Guid.NewGuid();
        var (session, created) = await sessions.GetOrCreateAsync(request.JobId, profile, proposed, cancellationToken).ConfigureAwait(false);
        var fresh = request.Profile is null
            // The job's own session is missing only if its first turn ran on another profile (a job from before profiles has no rows at all).
            ? created && request.Resume && (await sessions.ProfilesAsync(request.JobId, cancellationToken).ConfigureAwait(false)).Any(p => p != profile)
            : created;
        if (!fresh)
        {
            var resume = request.Profile is null ? request : request with { Session = new ClaudeSessionId(session), Resume = true };
            return switched ? resume with { Prompt = $"{CatchUp}\n\n{request.Prompt}" } : resume;
        }

        var job = await jobs.GetAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        var plan = await plans.GetAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        return request with { Session = new ClaudeSessionId(session), Resume = false, Prompt = Handoff(job?.WorkItemId.Value, job?.Title, job?.Branch?.Value, plan, request.Prompt) };
    }

    public const string CatchUp =
        "Note: since your last turn, other turns of this job ran in another session. Check `git log` and `git status` before you continue.";

    /// <summary>The first prompt of a session that starts mid-job.</summary>
    public static string Handoff(int? workItem, string? title, string? branch, string? plan, string turn) =>
        $"""
        You're taking over work item #{workItem}{(string.IsNullOrWhiteSpace(title) ? string.Empty : $" \"{title}\"")} from another session of agentd, on branch `{branch}` in this worktree.
        Earlier steps ran with a different model. Their work is on the branch: read `git log` and `git diff` against the base branch before you change anything. agentd's tools (set_phase, ask_developer, finish, …) work as before.
        {(string.IsNullOrWhiteSpace(plan) ? "There is no stored plan: work from the work item and the branch." : $"The approved plan (follow it step by step, in order; don't change files it doesn't name or cross its guardrails; if a step can't work as written, ask with ask_developer instead of improvising):\n\n{plan}")}

        Your turn:

        {turn}
        """;
}
