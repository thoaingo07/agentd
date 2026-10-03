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

    /// <summary>The knowledge hand-off after the PR was merged.</summary>
    public HandoffStatus Handoff { get; private set; }

    /// <summary>Review rounds the agent ran to address PR feedback.</summary>
    public int FixRounds { get; private set; }

    /// <summary>Which PR comments were already handled, and whether "ready to complete" was announced.</summary>
    public ReviewState Review { get; private set; } = ReviewState.Empty;

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
            FixRounds = s.FixRounds,
            Handoff = s.Handoff,
            Review = s.Review ?? ReviewState.Empty,
            WaitReminders = s.WaitReminders,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            Version = s.Version,
        };
    }

    /// <summary>Current state as a snapshot, for storage.</summary>
    public JobSnapshot ToSnapshot() => new(
        Id, WorkItemId, Repository, Title, State, Branch, Worktree, Session, PullRequest, Draft,
        Attempt, ResumeCount, PublishAttempts, LastError, NotBefore, CreatedAt, UpdatedAt, Version, PendingMessages, WaitingSince, WaitReminders, PlanStatus, Estimate, FixRounds, Review, Handoff);

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

    /// <summary>The PR is open (or updated after a fix round): watch it for review feedback.</summary>
    public Result OpenForReview(PullRequestUrl url)
    {
        if (Require("open for review", JobState.Publishing) is { } error)
        {
            return error;
        }

        PullRequest = url;
        LastError = null;
        NotBefore = null;
        Review = Review with { ReadyAnnounced = false };
        Transition(JobState.InReview, new PullRequestCreated(url, Now));
        return Result.Ok;
    }

    /// <summary>New review comments: the agent resumes to address them (they are queued as its next turn).</summary>
    public Result StartFixRound(IReadOnlyList<string> feedback, IReadOnlyList<int> commentIds, IReadOnlyList<int>? threadIds = null)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(commentIds);
        if (Require("start a fix round", JobState.InReview) is { } error)
        {
            return error;
        }

        if (feedback.Count == 0)
        {
            return DomainError.Validation("A fix round needs feedback.");
        }

        FixRounds++;
        PendingMessages = [.. PendingMessages, .. feedback];
        Review = new ReviewState([.. Review.SeenCommentIds, .. commentIds], false, threadIds ?? []);
        Transition(JobState.Running, new FixRoundStarted(FixRounds, feedback.Count, Now));
        return Result.Ok;
    }

    /// <summary>The PR threads the last fix round addressed (to reply on after the push); clears them.</summary>
    public IReadOnlyList<int> TakeRoundThreads()
    {
        var threads = Review.RoundThreads ?? [];
        Review = Review with { RoundThreads = [] };
        return threads;
    }

    /// <summary>Records comments that need no fix round (e.g. already resolved when first seen).</summary>
    public void MarkCommentsSeen(IReadOnlyList<int> commentIds)
    {
        ArgumentNullException.ThrowIfNull(commentIds);
        Review = Review with { SeenCommentIds = [.. Review.SeenCommentIds, .. commentIds.Except(Review.SeenCommentIds)] };
    }

    /// <summary>All review threads are resolved: announce "ready to complete" once per round.</summary>
    public Result AnnounceReady()
    {
        if (Require("announce ready", JobState.InReview) is { } error)
        {
            return error;
        }

        if (Review.ReadyAnnounced)
        {
            return DomainError.Conflict("Already announced.");
        }

        Review = Review with { ReadyAnnounced = true };
        UpdatedAt = Now;
        Raise(new ReadyToComplete(Now));
        return Result.Ok;
    }

    /// <summary>The developer completed (merged) the pull request.</summary>
    public Result Merged()
    {
        if (Require("record the merge", JobState.InReview) is { } error)
        {
            return error;
        }

        Transition(JobState.Done, new PullRequestMerged(PullRequest!.Value, Now));
        return Result.Ok;
    }

    /// <summary>
    /// The PR was merged: hand off the knowledge and learnings. The job runs again (same session) on
    /// <paramref name="knowledgeBranch"/> in <paramref name="worktree"/>, read-only until the developer agrees.
    /// </summary>
    public Result StartHandoff(BranchName knowledgeBranch, WorktreePath worktree)
    {
        if (State is not (JobState.InReview or JobState.Done) || PullRequest is null || Session is null)
        {
            return DomainError.InvalidTransition(State, "start the hand-off");
        }

        if (Handoff != HandoffStatus.None)
        {
            return DomainError.Conflict("The hand-off already ran for this job.");
        }

        Branch = knowledgeBranch;
        Worktree = worktree;
        Handoff = HandoffStatus.Requested;
        Review = ReviewState.Empty;
        Transition(JobState.Running, new HandoffStarted(knowledgeBranch, Now));
        return Result.Ok;
    }

    /// <summary>The hand-off turn starts: the agent proposes (read-only) until the developer agrees.</summary>
    public bool BeginProposing()
    {
        if (Handoff != HandoffStatus.Requested)
        {
            return false;
        }

        Handoff = HandoffStatus.Proposing;
        return true;
    }

    /// <summary>The developer agreed with the proposed knowledge changes: the agent may write them.</summary>
    public Result AgreeHandoff(string by)
    {
        if (Handoff != HandoffStatus.Proposing)
        {
            return DomainError.InvalidTransition(Handoff, "agree the hand-off");
        }

        Handoff = HandoffStatus.Agreed;
        UpdatedAt = Now;
        Raise(new HandoffAgreed(by, Now));
        return Result.Ok;
    }

    /// <summary>The developer doesn't want the knowledge synced: nothing is written; close-out follows.</summary>
    public Result DeclineHandoff(string by)
    {
        if (Handoff != HandoffStatus.Proposing || State != JobState.WaitingForHuman)
        {
            return DomainError.InvalidTransition(Handoff, "decline the hand-off");
        }

        Handoff = HandoffStatus.Declined;
        UpdatedAt = Now;
        Raise(new HandoffDeclined(by, Now));
        return Result.Ok;
    }

    /// <summary>Close-out: ask the developer whether to delete the thread (after the sync PR merged, or a decline).</summary>
    public Result RequestCloseOut(string question, IReadOnlyList<string> options)
    {
        if (State is not (JobState.InReview or JobState.WaitingForHuman or JobState.Running) || Handoff is not (HandoffStatus.Agreed or HandoffStatus.Declined))
        {
            return DomainError.InvalidTransition(State, "close out");
        }

        Handoff = HandoffStatus.Closing;
        WaitingSince = Now;
        WaitReminders = 0;
        Transition(JobState.WaitingForHuman, new DeveloperQuestionAsked(question, options, Now));
        return Result.Ok;
    }

    /// <summary>The developer answered the close-out question: the job is done.</summary>
    public Result CloseOut(bool threadDeleted)
    {
        if (Handoff != HandoffStatus.Closing || State != JobState.WaitingForHuman)
        {
            return DomainError.InvalidTransition(State, "finish the close-out");
        }

        WaitingSince = null;
        Transition(JobState.Done, new ClosedOut(threadDeleted, Now));
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
    PlanEstimate? Estimate = null,
    int FixRounds = 0,
    ReviewState? Review = null,
    HandoffStatus Handoff = HandoffStatus.None);

/// <summary>
/// The knowledge hand-off: Requested (waiting for a turn) → Proposing (read-only, agreeing with the developer)
/// → Agreed (writing the sync PR) or Declined. Done when the sync PR is merged or nothing is synced.
/// </summary>
public enum HandoffStatus
{
    None,
    Requested,
    Proposing,
    Agreed,
    Declined,

    /// <summary>All done: waiting for the developer to delete (or keep) the thread.</summary>
    Closing,
}

/// <summary>PR review tracking: comment ids already handled, and whether "ready to complete" was posted for the current round.</summary>
public sealed record ReviewState(IReadOnlyList<int> SeenCommentIds, bool ReadyAnnounced, IReadOnlyList<int>? RoundThreads = null)
{
    public static ReviewState Empty { get; } = new([], false);
}

/// <summary>Plan approval: required for most jobs (the agent works read-only until approved).</summary>
public enum PlanStatus
{
    NotRequired,
    Pending,
    Approved,
}

/// <summary>The agent's estimate: time and share of the 5-hour usage window, plus the usage when it planned.</summary>
public sealed record PlanEstimate(int Minutes, int UsagePercent, double? UsageAtPlan, DateTimeOffset SubmittedAt, DateTimeOffset? ApprovedAt = null);
