using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Domain.Jobs.Events;

public sealed record JobQueued(WorkItemId WorkItemId, RepositoryName Repository, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JobPreparing(DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JobStarted(BranchName Branch, WorktreePath Worktree, ClaudeSessionId Session, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JobFinished(string PullRequestTitle, string Summary, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PullRequestCreated(PullRequestUrl Url, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JobFailed(string Reason, JobState FromState, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JobCancelled(string By, JobState FromState, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JobRetried(int Attempt, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JobRecovered(int ResumeCount, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JobRequeued(string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record JobDeferred(DateTimeOffset? NotBefore, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PublishRetryScheduled(int Attempt, string Reason, DateTimeOffset RetryAt, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record FixRoundStarted(int Round, int Comments, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ReadyToComplete(DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PullRequestMerged(PullRequestUrl Url, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record HandoffStarted(BranchName Branch, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record HandoffAgreed(string By, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record HandoffDeclined(string By, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ClosedOut(bool ThreadDeleted, DateTimeOffset OccurredAt) : IDomainEvent;
