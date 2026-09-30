using Agentd.Domain.Common;
using Agentd.Domain.Jobs.Events;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Domain.Jobs;

/// <summary>
/// One agent run for one work item. State changes only through the methods below; each valid change
/// raises exactly one domain event, and an invalid one returns <see cref="DomainError.InvalidTransition"/>
/// without changing anything.
/// </summary>
public sealed class Job : AggregateRoot<JobId>
{
    private readonly IClock _clock;

    private Job(IClock clock) => _clock = clock;

    public WorkItemId WorkItemId { get; private set; }

    public RepositoryName Repository { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public JobState State { get; private set; }

    public BranchName? Branch { get; private set; }

    public WorktreePath? Worktree { get; private set; }

    public ClaudeSessionId? Session { get; private set; }

    public PullRequestDraft? Draft { get; private set; }

    public PullRequestUrl? PullRequest { get; private set; }

    public int Attempt { get; private set; }

    public int ResumeCount { get; private set; }

    /// <summary>Failed attempts to push/open the pull request while <see cref="JobState.Publishing"/>.</summary>
    public int PublishAttempts { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Earliest time a deferred job may start again (e.g. after a usage limit resets).</summary>
    public DateTimeOffset? NotBefore { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic-concurrency version as last read from / written to storage (0 = new).</summary>
    public long Version { get; private set; }

    /// <summary>A new job for a claimed work item, in <see cref="JobState.Queued"/>.</summary>
    public static Job Create(WorkItemId workItem, RepositoryName repository, string title, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.UtcNow;
        var job = new Job(clock)
        {
            WorkItemId = workItem,
            Repository = repository,
            Title = title?.Trim() ?? string.Empty,
            State = JobState.Queued,
            Attempt = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        job.Raise(new JobQueued(workItem, repository, now));
        return job;
    }

    /// <summary>Rebuilds a job from storage. Raises no events.</summary>
    public static Job Rehydrate(JobSnapshot s, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new Job(clock)
        {
            Id = s.Id,
            WorkItemId = s.WorkItemId,
            Repository = s.Repository,
            Title = s.Title,
            State = s.State,
            Branch = s.Branch,
            Worktree = s.Worktree,
            Session = s.Session,
            PullRequest = s.PullRequest,
            Draft = s.Draft,
            Attempt = s.Attempt,
            ResumeCount = s.ResumeCount,
            PublishAttempts = s.PublishAttempts,
            LastError = s.LastError,
            NotBefore = s.NotBefore,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            Version = s.Version,
        };
    }

    /// <summary>Current state as a snapshot, for storage.</summary>
    public JobSnapshot ToSnapshot() => new(
        Id, WorkItemId, Repository, Title, State, Branch, Worktree, Session, PullRequest, Draft,
        Attempt, ResumeCount, PublishAttempts, LastError, NotBefore, CreatedAt, UpdatedAt, Version);

    /// <summary>Called by storage after an insert or save.</summary>
    public void Persisted(JobId id, long version)
    {
        Id = id;
        Version = version;
    }

    public Result BeginPreparing()
    {
        if (Require("begin preparing", JobState.Queued) is { } error)
        {
            return error;
        }

        Transition(JobState.Preparing, new JobPreparing(Now));
        return Result.Ok;
    }

    public Result Start(WorktreePath worktree, BranchName branch, ClaudeSessionId session)
    {
        if (Require("start", JobState.Preparing) is { } error)
        {
            return error;
        }

        Worktree = worktree;
        Branch = branch;
        Session = session;
        NotBefore = null;
        Transition(JobState.Running, new JobStarted(branch, worktree, session, Now));
        return Result.Ok;
    }

    public Result Finish(PullRequestDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (Require("finish", JobState.Running) is { } error)
        {
            return error;
        }

        Draft = draft;
        Transition(JobState.Publishing, new JobFinished(draft.Title, draft.Summary, Now));
        return Result.Ok;
    }

    public Result Complete(PullRequestUrl url)
    {
        if (Require("complete", JobState.Publishing) is { } error)
        {
            return error;
        }

        PullRequest = url;
        LastError = null;
        NotBefore = null;
        Transition(JobState.Done, new PullRequestCreated(url, Now));
        return Result.Ok;
    }

    public Result Fail(string reason)
    {
        if (State.IsTerminal())
        {
            return DomainError.InvalidTransition(State, "fail");
        }

        var from = State;
        LastError = reason;
        Transition(JobState.Failed, new JobFailed(reason, from, Now));
        return Result.Ok;
    }

    public Result Cancel(string by)
    {
        if (State.IsTerminal())
        {
            return DomainError.InvalidTransition(State, "cancel");
        }

        var from = State;
        Transition(JobState.Cancelled, new JobCancelled(by, from, Now));
        return Result.Ok;
    }

    public Result Retry()
    {
        if (Require("retry", JobState.Failed) is { } error)
        {
            return error;
        }

        Attempt++;
        LastError = null;
        Transition(JobState.Queued, new JobRetried(Attempt, Now));
        return Result.Ok;
    }

    /// <summary>
    /// A push or pull-request API call failed. The job stays in <see cref="JobState.Publishing"/> and is
    /// retried after <paramref name="retryAt"/>, until <paramref name="maxAttempts"/> failures, then it fails.
    /// </summary>
    public Result PublishFailed(string reason, int maxAttempts, DateTimeOffset retryAt)
    {
        if (Require("record a publish failure", JobState.Publishing) is { } error)
        {
            return error;
        }

        PublishAttempts++;
        if (PublishAttempts >= maxAttempts)
        {
            return Fail($"Publishing failed after {PublishAttempts} attempts: {reason}");
        }

        LastError = reason;
        NotBefore = retryAt;
        Raise(new PublishRetryScheduled(PublishAttempts, reason, retryAt, Now));
        UpdatedAt = Now;
        return Result.Ok;
    }

    /// <summary>
    /// Puts a running job back in the queue without losing its session (e.g. the Claude subscription
    /// hit its usage limit). It resumes with the same session once <paramref name="notBefore"/> passes.
    /// </summary>
    public Result Defer(DateTimeOffset? notBefore, string reason)
    {
        if (Require("defer", JobState.Running) is { } error)
        {
            return error;
        }

        NotBefore = notBefore;
        LastError = reason;
        Transition(JobState.Queued, new JobDeferred(notBefore, reason, Now));
        return Result.Ok;
    }

    /// <summary>
    /// Puts a job that was interrupted while being prepared (e.g. agentd restarted) back in the queue.
    /// Preparation is idempotent: the worktree is re-created or reused.
    /// </summary>
    public Result Requeue(string reason)
    {
        if (Require("requeue", JobState.Preparing) is { } error)
        {
            return error;
        }

        Transition(JobState.Queued, new JobRequeued(reason, Now));
        return Result.Ok;
    }

    /// <summary>Records that a running job is being resumed after the daemon restarted.</summary>
    public Result MarkRecovered()
    {
        if (Require("recover", JobState.Running) is { } error)
        {
            return error;
        }

        ResumeCount++;
        Transition(JobState.Running, new JobRecovered(ResumeCount, Now));
        return Result.Ok;
    }

    private DateTimeOffset Now => _clock.UtcNow;

    private DomainError? Require(string operation, params JobState[] allowed) =>
        allowed.Contains(State) ? null : DomainError.InvalidTransition(State, operation);

    private void Transition(JobState to, IDomainEvent domainEvent)
    {
        State = to;
        UpdatedAt = domainEvent.OccurredAt;
        Raise(domainEvent);
    }
}

/// <summary>Stored state of a job, used to rehydrate the aggregate (and produced by <see cref="Job.ToSnapshot"/>).</summary>
public sealed record JobSnapshot(
    JobId Id,
    WorkItemId WorkItemId,
    RepositoryName Repository,
    string Title,
    JobState State,
    BranchName? Branch,
    WorktreePath? Worktree,
    ClaudeSessionId? Session,
    PullRequestUrl? PullRequest,
    PullRequestDraft? Draft,
    int Attempt,
    int ResumeCount,
    int PublishAttempts,
    string? LastError,
    DateTimeOffset? NotBefore,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);
