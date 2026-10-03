using Agentd.Domain.Common;
using Agentd.Domain.Jobs.Events;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

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

    /// <summary>Whether the plan needs the developer's approval before the agent may edit.</summary>
    public PlanStatus PlanStatus { get; private set; }

    /// <summary>The agent's estimate from its plan (null until it submits one).</summary>
    public PlanEstimate? Estimate { get; private set; }

    /// <summary>When the job started waiting for the developer (null unless <see cref="JobState.WaitingForHuman"/>).</summary>
    public DateTimeOffset? WaitingSince { get; private set; }

    /// <summary>Reminders already posted during the current wait.</summary>
    public int WaitReminders { get; private set; }

    /// <summary>Developer replies received while the agent was running, delivered as its next turn.</summary>
    public IReadOnlyList<string> PendingMessages { get; private set; } = [];

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
            PendingMessages = s.PendingMessages ?? [],
            WaitingSince = s.WaitingSince,
            PlanStatus = s.PlanStatus,
            Estimate = s.Estimate,
            WaitReminders = s.WaitReminders,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            Version = s.Version,
        };
    }

    /// <summary>Current state as a snapshot, for storage.</summary>
    public JobSnapshot ToSnapshot() => new(
        Id, WorkItemId, Repository, Title, State, Branch, Worktree, Session, PullRequest, Draft,
        Attempt, ResumeCount, PublishAttempts, LastError, NotBefore, CreatedAt, UpdatedAt, Version, PendingMessages, WaitingSince, WaitReminders, PlanStatus, Estimate);

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

    public Result Start(WorktreePath worktree, BranchName branch, ClaudeSessionId session, bool requirePlanApproval = false)
    {
        if (Require("start", JobState.Preparing) is { } error)
        {
            return error;
        }

        if (requirePlanApproval && PlanStatus == PlanStatus.NotRequired && Session is null)
        {
            PlanStatus = PlanStatus.Pending;
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

    /// <summary>The agent asked the developer a question: the job waits for a reply.</summary>
    public Result AskDeveloper(string question, IReadOnlyList<string>? options = null)
    {
        if (Require("ask the developer", JobState.Running) is { } error)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            return DomainError.Validation("The question must not be empty.");
        }

        WaitingSince = Now;
        WaitReminders = 0;
        Transition(JobState.WaitingForHuman, new DeveloperQuestionAsked(question.Trim(), options ?? [], Now));
        return Result.Ok;
    }

    /// <summary>Records the agent's plan estimate (the plan text itself goes to the developer as a question or a message).</summary>
    public Result SubmitPlan(PlanEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (Require("submit a plan", JobState.Running) is { } error)
        {
            return error;
        }

        if (estimate.Minutes <= 0 || estimate.UsagePercent is < 0 or > 100)
        {
            return DomainError.Validation("The estimate needs positive minutes and a usage share of 0–100%.");
        }

        Estimate = estimate;
        UpdatedAt = Now;
        return Result.Ok;
    }

    /// <summary>The developer approved the plan: the agent may now edit.</summary>
    public Result ApprovePlan(string by)
    {
        if (PlanStatus != PlanStatus.Pending)
        {
            return DomainError.InvalidTransition(PlanStatus, "approve the plan");
        }

        PlanStatus = PlanStatus.Approved;
        Estimate = Estimate is null ? null : Estimate with { ApprovedAt = Now };
        UpdatedAt = Now;
        Raise(new PlanApproved(by, Now));
        return Result.Ok;
    }

    /// <summary>Posts reminder number <paramref name="reminder"/> of the current wait (once each).</summary>
    public Result RemindWaiting(int reminder, DateTimeOffset expiresAt)
    {
        if (Require("remind", JobState.WaitingForHuman) is { } error)
        {
            return error;
        }

        if (reminder <= WaitReminders)
        {
            return DomainError.Conflict($"Reminder {reminder} was already sent.");
        }

        WaitReminders = reminder;
        UpdatedAt = Now;
        Raise(new WaitReminderSent(reminder, expiresAt, Now));
        return Result.Ok;
    }

    /// <summary>
    /// A developer replied. The reply is queued in <see cref="PendingMessages"/> (as <c>from: reply</c>)
    /// and delivered as the agent's next turn; a waiting job also goes back to <see cref="JobState.Running"/>.
    /// </summary>
    public Result ResumeWith(string reply, string from, ProviderKey? via = null)
    {
        if (Require("deliver a developer reply", JobState.WaitingForHuman, JobState.Running) is { } error)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(reply))
        {
            return DomainError.Validation("The reply must not be empty.");
        }

        PendingMessages = [.. PendingMessages, $"{from}: {reply.Trim()}"];
        if (State == JobState.WaitingForHuman)
        {
            WaitingSince = null;
            WaitReminders = 0;
            Transition(JobState.Running, new DeveloperReplied(reply, from, Resumed: true, Now, via));
            return Result.Ok;
        }

        UpdatedAt = Now;
        Raise(new DeveloperReplied(reply, from, Resumed: false, Now, via));
        return Result.Ok;
    }

    /// <summary>Returns and clears the queued developer replies (they are being delivered to the agent).</summary>
    public IReadOnlyList<string> TakePendingMessages()
    {
        var messages = PendingMessages;
        PendingMessages = [];
        return messages;
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
    long Version,
    IReadOnlyList<string>? PendingMessages = null,
    DateTimeOffset? WaitingSince = null,
    int WaitReminders = 0,
    PlanStatus PlanStatus = PlanStatus.NotRequired,
    PlanEstimate? Estimate = null);

/// <summary>Plan approval: required for most jobs (the agent works read-only until approved).</summary>
public enum PlanStatus
{
    NotRequired,
    Pending,
    Approved,
}

/// <summary>The agent's estimate: time and share of the 5-hour usage window, plus the usage when it planned.</summary>
public sealed record PlanEstimate(int Minutes, int UsagePercent, double? UsageAtPlan, DateTimeOffset SubmittedAt, DateTimeOffset? ApprovedAt = null);
