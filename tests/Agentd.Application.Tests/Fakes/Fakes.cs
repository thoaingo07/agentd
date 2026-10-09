using System.Collections.Concurrent;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Tests.Fakes;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
}

/// <summary>In-memory jobs with version checks, mirroring the PostgreSQL routines' behavior.</summary>
internal sealed class InMemoryJobs(IClock clock) : IJobRepository
{
    private readonly ConcurrentDictionary<long, JobSnapshot> _rows = new();
    private long _nextId;

    public List<string> SavedEventTypes { get; } = [];

    public List<IDomainEvent> SavedEvents { get; } = [];

    public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(id.Value, out var s) ? Job.Rehydrate(s, clock) : null);

    public Task<Job?> FindActiveByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.Values.Where(s => s.WorkItemId == workItem && !s.State.IsTerminal()).Select(s => Job.Rehydrate(s, clock)).FirstOrDefault());

    public Task<Result> AddAsync(Job job, CancellationToken cancellationToken)
    {
        if (_rows.Values.Any(s => s.WorkItemId == job.WorkItemId && !s.State.IsTerminal()))
        {
            return Task.FromResult(Result.Fail(DomainError.Conflict("active job exists")));
        }

        job.Persisted(new JobId(Interlocked.Increment(ref _nextId)), 1);
        Store(job);
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> SaveAsync(Job job, CancellationToken cancellationToken)
    {
        lock (_rows)
        {
            if (!_rows.TryGetValue(job.Id.Value, out var current) || current.Version != job.Version)
            {
                return Task.FromResult(Result.Fail(DomainError.Conflict("version changed")));
            }

            job.Persisted(job.Id, job.Version + 1);
            Store(job);
            return Task.FromResult(Result.Ok);
        }
    }

    public Task<Job?> DequeueNextAsync(string worker, CancellationToken cancellationToken)
    {
        var next = _rows.Values
            .Where(s => s.State == JobState.Queued && (s.NotBefore is null || s.NotBefore <= clock.UtcNow))
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id.Value)
            .FirstOrDefault();
        if (next is null)
        {
            return Task.FromResult<Job?>(null);
        }

        // Mirrors agentd.job_dequeue: atomically Queued → Preparing.
        var job = Job.Rehydrate(next, clock);
        job.BeginPreparing();
        job.Persisted(job.Id, job.Version + 1);
        Store(job);
        return Task.FromResult<Job?>(job);
    }

    public Task<IReadOnlyList<Job>> ListByStateAsync(IReadOnlyCollection<JobState> states, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Job>>(_rows.Values.Where(s => states.Contains(s.State)).Select(s => Job.Rehydrate(s, clock)).ToList());

    public Task<IReadOnlyList<Job>> ListRecentAsync(TimeSpan window, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Job>>(_rows.Values.Where(s => s.CreatedAt >= clock.UtcNow - window).Select(s => Job.Rehydrate(s, clock)).ToList());

    public Job Single() => Job.Rehydrate(_rows.Values.Single(), clock);

    /// <summary>Test hook: simulate a lost save by rewriting the stored state.</summary>
    public void ForceState(JobId id, JobState state) => _rows[id.Value] = _rows[id.Value] with { State = state };

    public Job Get(JobId id) => Job.Rehydrate(_rows[id.Value], clock);

    public Job? TryGet(JobId id) => _rows.TryGetValue(id.Value, out var s) ? Job.Rehydrate(s, clock) : null;

    private void Store(Job job)
    {
        var events = job.DequeueEvents();
        SavedEvents.AddRange(events);
        SavedEventTypes.AddRange(events.Select(e => e.GetType().Name));
        _rows[job.Id.Value] = job.ToSnapshot();
    }
}

internal sealed class FakeWorkItems : IWorkItemSource
{
    public Dictionary<int, WorkItemDetails> Items { get; } = [];

    public List<(int Id, string Text)> Comments { get; } = [];

    public HashSet<int> Claimed { get; } = [];

    public bool LoseClaimRace { get; set; }

    public Task<IReadOnlyList<WorkItemRef>> QueryTaggedAsync(string tag, string excludeTag, IReadOnlyList<string> states, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkItemRef>>(Items.Values
            .Where(i => i.Tags.Contains(tag) && !Claimed.Contains(i.Id) && states.Contains(i.State))
            .Select(i => new WorkItemRef(i.Id, i.Rev)).ToList());

    public Task<WorkItemDetails?> GetAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.TryGetValue(id, out var i) ? i : null);

    public Task<bool> TryClaimAsync(int id, int rev, string claimTag, CancellationToken cancellationToken)
    {
        if (LoseClaimRace)
        {
            return Task.FromResult(false);
        }

        Claimed.Add(id);
        return Task.FromResult(true);
    }

    /// <summary>Who the calls run as, recorded per comment.</summary>
    public Func<Guid?>? ActingAs { get; set; }

    public List<Guid?> CommentedAs { get; } = [];

    public Task AddCommentAsync(int id, string text, CancellationToken cancellationToken)
    {
        Comments.Add((id, text));
        CommentedAs.Add(ActingAs?.Invoke());
        if (Items.TryGetValue(id, out var item))
        {
            Items[id] = item with { Comments = [.. item.Comments, new WorkItemComment("agentd", DateTimeOffset.UnixEpoch, text)] };
        }

        return Task.CompletedTask;
    }

    public List<NewWorkItem> Created { get; } = [];

    public Task<CreatedWorkItem> CreateAsync(NewWorkItem item, CancellationToken cancellationToken)
    {
        Created.Add(item);
        var id = 9000 + Created.Count;
        return Task.FromResult(new CreatedWorkItem(id, new Uri($"https://dev.azure.com/ermsystem/Portal/_workitems/edit/{id}")));
    }

    public WorkItemDetails Add(int id, string title = "Fix login", string areaPath = "Portal\\Platform", params string[] tags)
    {
        var item = new WorkItemDetails(id, 3, title, "Active", areaPath, tags.Length == 0 ? ["ai-workflow"] : tags,
            "The login redirect loops.", "Redirects once.", null, [], new Uri($"https://dev.azure.com/ermsystem/Portal/_workitems/edit/{id}"));
        Items[id] = item;
        return item;
    }
}

internal sealed class FakeRegistry : IRepositoryRegistry
{
    public List<Repository> Repositories { get; } =
    [
        new(RepositoryName.From("sysmin"), "git@erm-azdo:v3/ermsystem/Portal/sysmin", new AzureDevOpsRepo("ermsystem", "Portal", "sysmin"), "develop", "repo:sysmin", ["Portal\\Platform"]),
    ];

    public Task<IReadOnlyList<Repository>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Repository>>(Repositories);

    public Task<Repository?> GetAsync(RepositoryName name, CancellationToken cancellationToken) =>
        Task.FromResult(Repositories.FirstOrDefault(r => r.Name == name));

    public Task<Result> UpsertAsync(Repository repository, CancellationToken cancellationToken)
    {
        if (Repositories.Any(r => r.Name != repository.Name && string.Equals(r.RemoteUrl, repository.RemoteUrl, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(Result.Fail(DomainError.Conflict("url in use")));
        }

        Repositories.RemoveAll(r => r.Name == repository.Name);
        Repositories.Add(repository);
        return Task.FromResult(Result.Ok);
    }

    public Task<bool> RemoveAsync(RepositoryName name, CancellationToken cancellationToken) =>
        Task.FromResult(Repositories.RemoveAll(r => r.Name == name) > 0);
}

internal sealed class FakeGitRemote : IGitRemote
{
    public string DefaultBranch { get; set; } = "develop";

    public bool Unreachable { get; set; }

    public Task<string> GetDefaultBranchAsync(string remoteUrl, CancellationToken cancellationToken) =>
        Unreachable ? throw new InvalidOperationException("Permission denied (publickey)") : Task.FromResult(DefaultBranch);
}

internal sealed class FakeWorktrees : IWorktreeManager
{
    public List<string> Created { get; } = [];

    public List<string> Cloned { get; } = [];

    public List<string> Pushed { get; } = [];

    public bool HasCommits { get; set; } = true;

    public bool FailCreate { get; set; }

    /// <summary>Number of upcoming pushes that fail (transient remote errors).</summary>
    public int FailPushes { get; set; }

    public List<string> Removed { get; } = [];

    public Task EnsureCloneAsync(Repository repository, CancellationToken cancellationToken)
    {
        Cloned.Add(repository.Name.Value);
        return Task.CompletedTask;
    }

    public Task<WorktreePath> CreateAsync(Repository repository, WorkItemId workItem, BranchName branch, CancellationToken cancellationToken)
    {
        if (FailCreate)
        {
            throw new InvalidOperationException("git worktree add failed");
        }

        Created.Add(branch.Value);
        return Task.FromResult(new WorktreePath($"/home/agentd/.agentd/worktrees/{repository.Name}/wi-{workItem}"));
    }

    public List<string> Recreated { get; } = [];

    public Task<WorktreePath> RecreateAsync(Repository repository, WorkItemId workItem, BranchName branch, CancellationToken cancellationToken)
    {
        Recreated.Add(branch.Value);
        return Task.FromResult(new WorktreePath($"/home/agentd/.agentd/worktrees/{repository.Name}/wi-{workItem}"));
    }

    public Task<bool> HasCommitsAheadAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken) => Task.FromResult(HasCommits);

    public Task PushAsync(WorktreePath worktree, BranchName branch, CancellationToken cancellationToken)
    {
        if (FailPushes > 0)
        {
            FailPushes--;
            throw new InvalidOperationException("remote hung up unexpectedly");
        }

        Pushed.Add(branch.Value);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken)
    {
        Removed.Add(worktree.Value);
        return Task.CompletedTask;
    }

    public List<string> Pruned { get; } = [];

    public Task PruneAsync(Repository repository, CancellationToken cancellationToken)
    {
        Pruned.Add(repository.Name.Value);
        return Task.CompletedTask;
    }

    public BranchDiff? Diff { get; set; }

    public List<(string Branch, string? Worktree, int MaxBytes)> Diffed { get; } = [];

    public List<string> Detached { get; } = [];

    public Task<string> CheckoutDetachedAsync(Repository repository, string name, CancellationToken cancellationToken)
    {
        Detached.Add(name);
        return Task.FromResult($"/home/agentd/.agentd/worktrees/{repository.Name}/{name}");
    }

    public List<(string Name, string Commit)> CheckedOutCommits { get; } = [];

    public List<string> Folders { get; } = [];

    public List<string> Files { get; } = [];

    public Task<IReadOnlyList<string>> ListFilesAsync(Repository repository, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([.. Files]);

    public Task<IReadOnlyList<WorktreeFolder>> ListFoldersAsync(Repository repository, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorktreeFolder>>([.. Folders.Select(f => new WorktreeFolder(f, $"/home/agentd/.agentd/worktrees/{repository.Name}/{f}"))]);

    public Task<string> CheckoutCommitAsync(Repository repository, string name, string commit, CancellationToken cancellationToken)
    {
        CheckedOutCommits.Add((name, commit));
        return Task.FromResult($"/home/agentd/.agentd/worktrees/{repository.Name}/{name}");
    }

    public Task<BranchDiff?> DiffAsync(Repository repository, BranchName branch, WorktreePath? worktree, int maxBytes, CancellationToken cancellationToken)
    {
        Diffed.Add((branch.Value, worktree?.Value, maxBytes));
        return Task.FromResult(Diff);
    }
}

internal sealed class FakePullRequests : IPullRequestService
{
    public PullRequestRef? Existing { get; set; }

    public List<(string Source, string Target, string Title)> Created { get; } = [];

    public Task<PullRequestRef?> FindOpenAsync(Repository repository, BranchName source, CancellationToken cancellationToken) => Task.FromResult(Existing);

    /// <summary>Who the calls run as (an <c>AdoActor</c>'s Current), recorded per created PR.</summary>
    public Func<Guid?>? ActingAs { get; set; }

    public List<Guid?> CreatedAs { get; } = [];

    public Task<PullRequestRef> CreateAsync(Repository repository, BranchName source, string target, string title, string description, WorkItemId workItem, CancellationToken cancellationToken)
    {
        Created.Add((source.Value, target, title));
        CreatedAs.Add(ActingAs?.Invoke());
        return Task.FromResult(new PullRequestRef(77, new Uri("https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/77")));
    }

    public PullRequestStatus Status { get; set; } = PullRequestStatus.Active;

    public List<PullRequestComment> Comments { get; } = [];

    public List<(int Pr, int Thread, string Text)> Replies { get; } = [];

    public Task<PullRequestStatus> GetStatusAsync(Repository repository, int pullRequestId, CancellationToken cancellationToken) => Task.FromResult(Status);

    public Task<IReadOnlyList<PullRequestComment>> ListCommentsAsync(Repository repository, int pullRequestId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PullRequestComment>>(Comments.ToList());

    public Task ReplyAsync(Repository repository, int pullRequestId, int threadId, string text, CancellationToken cancellationToken)
    {
        Replies.Add((pullRequestId, threadId, text));
        return Task.CompletedTask;
    }

    public Dictionary<int, PullRequestDetails> Details { get; } = [];

    public List<(int PullRequestId, string Text, string? File, int? Line)> Threads { get; } = [];

    public Task<PullRequestDetails?> GetAsync(Repository repository, int pullRequestId, CancellationToken cancellationToken) =>
        Task.FromResult(Details.TryGetValue(pullRequestId, out var pr) ? pr : null);

    public Task<int> CreateThreadAsync(Repository repository, int pullRequestId, string text, string? filePath, int? line, CancellationToken cancellationToken)
    {
        Threads.Add((pullRequestId, text, filePath, line));
        return Task.FromResult(100 + Threads.Count);
    }

    /// <summary>The latest text of each thread agentd edited (its main message).</summary>
    public Dictionary<int, string> Edited { get; } = [];

    public Dictionary<int, PullRequestThreadStatus> ThreadStatus { get; } = [];

    public Task UpdateThreadTextAsync(Repository repository, int pullRequestId, int threadId, string text, CancellationToken cancellationToken)
    {
        Edited[threadId] = text;
        return Task.CompletedTask;
    }

    public Task SetThreadStatusAsync(Repository repository, int pullRequestId, int threadId, PullRequestThreadStatus status, CancellationToken cancellationToken)
    {
        ThreadStatus[threadId] = status;
        return Task.CompletedTask;
    }
}

internal sealed class FakeRunner : IAgentRunner
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<AgentRunOutcome>> _held = new();
    private readonly ConcurrentQueue<AgentRunRequest> _started = new();

    public HashSet<long> Running { get; } = [];

    public List<long> CancelledJobs { get; } = [];

    /// <summary>When true, each run blocks until <see cref="Release"/> (or its token is cancelled).</summary>
    public bool Hold { get; set; }

    public IReadOnlyList<AgentRunRequest> Started => [.. _started];

    public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        _started.Enqueue(request);
        if (!Hold)
        {
            return new AgentRunOutcome.Exited(0, null);
        }

        var held = _held.GetOrAdd(request.JobId.Value, _ => new TaskCompletionSource<AgentRunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
        await using var registration = cancellationToken.Register(() => held.TrySetResult(new AgentRunOutcome.Cancelled()));
        return await held.Task;
    }

    public void Release(JobId jobId, AgentRunOutcome outcome) =>
        _held.GetOrAdd(jobId.Value, _ => new TaskCompletionSource<AgentRunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(outcome);

    public bool IsRunning(JobId jobId) => Running.Contains(jobId.Value);

    public void Cancel(JobId jobId) => CancelledJobs.Add(jobId.Value);
}

internal sealed class FakeEvents : IEventStore
{
    public List<(JobId? Job, string Type, string Payload)> Appended { get; } = [];

    public Task<long> AppendAsync(JobId? jobId, string type, string payloadJson, CancellationToken cancellationToken)
    {
        Appended.Add((jobId, type, payloadJson));
        return Task.FromResult((long)Appended.Count);
    }
}

internal sealed class FakeConversations(Func<JobId, WorkItemId?>? workItemOf = null) : IConversationStore
{
    private long _nextId;

    public List<Conversation> All { get; } = [];

    public Task<Result> AddAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        conversation.Persisted(new ConversationId(Interlocked.Increment(ref _nextId)));
        All.Add(conversation);
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> SaveAsync(Conversation conversation, CancellationToken cancellationToken) => Task.FromResult(Result.Ok);

    public Task<IReadOnlyList<Conversation>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Conversation>>(All.Where(c => c.JobId == jobId).ToList());

    public Task<Conversation?> FindExternalAsync(ProviderKey provider, string externalConversationId, CancellationToken cancellationToken) =>
        Task.FromResult(All.FirstOrDefault(c => c.Provider == provider && c.ExternalConversationId == externalConversationId));

    public Task<IReadOnlyList<Conversation>> ListOpenByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Conversation>>(All.Where(c => c.IsOpen && workItemOf?.Invoke(c.JobId) == workItem).ToList());

    public Task<Result> MoveAsync(Conversation conversation, JobId jobId, CancellationToken cancellationToken)
    {
        conversation.MovedTo(jobId);
        return Task.FromResult(Result.Ok);
    }

    public Task<IReadOnlyList<Conversation>> ListOpenAsync(ProviderKey provider, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Conversation>>(All.Where(c => c.Provider == provider && c.IsOpen).ToList());
}

/// <summary>A chat provider that records what it was asked to do.</summary>
internal sealed class FakeChat(string key) : IMessagingProvider
{
    public ProviderKey Key { get; } = ProviderKey.From(key);

    public MessagingCapabilities Capabilities { get; } = new(2000, true, true, true, true);

    public List<ConversationSpec> Opened { get; } = [];

    public List<string> SentText { get; } = [];

    public List<string> Deleted { get; } = [];

    private int _nextMessage;

    public bool FailOpen { get; set; }

    public Task<ConversationRef> OpenConversationAsync(ConversationSpec spec, CancellationToken cancellationToken)
    {
        if (FailOpen)
        {
            throw new HttpRequestException("gateway unavailable");
        }

        Opened.Add(spec);
        return Task.FromResult(new ConversationRef(Key, $"thread-{spec.JobId}", "space"));
    }

    public Task<MessageRef> SendAsync(ConversationRef conversation, OutboundMessage message, CancellationToken cancellationToken)
    {
        SentText.Add(message.Markdown);
        return Task.FromResult(new MessageRef(conversation, $"m{++_nextMessage}"));
    }

    public Task DeleteAsync(MessageRef message, CancellationToken cancellationToken)
    {
        Deleted.Add(message.ExternalMessageId);
        return Task.CompletedTask;
    }

    public List<string> DeletedThreads { get; } = [];

    public List<string> ArchivedThreads { get; } = [];

    public List<string> ArchiveNotes { get; } = [];

    /// <summary>Simulates a bot without permission to delete threads (Discord: Manage Threads).</summary>
    public bool FailDeleteThread { get; set; }

    public Task DeleteConversationAsync(ConversationRef conversation, CancellationToken cancellationToken)
    {
        if (FailDeleteThread)
        {
            throw new MessagingDeliveryException("Discord returned 403: Missing Permissions", permanent: true);
        }

        DeletedThreads.Add(conversation.ExternalConversationId);
        return Task.CompletedTask;
    }

    public Task EditAsync(MessageRef message, OutboundMessage replacement, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseConversationAsync(ConversationRef conversation, string reason, CancellationToken cancellationToken)
    {
        ArchivedThreads.Add(conversation.ExternalConversationId);
        ArchiveNotes.Add(reason);
        return Task.CompletedTask;
    }

    public Uri? GetLink(ConversationRef conversation) => new($"https://chat.example/{conversation.ExternalConversationId}");

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new ProviderHealth(true, "ok"));
}

internal sealed class FakeOutbox : IOutbox
{
    public List<(JobId Job, OutboxMessage Message)> Enqueued { get; } = [];

    public Task EnqueueAsync(JobId jobId, IReadOnlyList<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        Enqueued.AddRange(messages.Select(m => (jobId, m)));
        return Task.CompletedTask;
    }
}

/// <summary>All fakes wired to real handlers.</summary>
internal sealed class TestContext
{
    public FakeClock Clock { get; } = new();

    public FakeWorkItems WorkItems { get; } = new();

    public FakeRegistry Registry { get; } = new();

    public FakeWorktrees Worktrees { get; } = new();

    public FakePullRequests PullRequests { get; } = new();

    public FakeRunner Runner { get; } = new();

    public FakeEvents Events { get; } = new();

    public InMemoryJobs Jobs { get; }

    /// <summary>Plan approval and the review loop are off by default here; their own tests turn them on.</summary>
    public IOptions<JobOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new JobOptions { RequirePlanApproval = false, ReviewLoop = false, Handoff = false });

    public NoMatchNotices Notices { get; } = new();

    public FakeConversations Conversations { get; }

    /// <summary>Registered chat providers; enable them in <see cref="Messaging"/>.</summary>
    public List<IMessagingProvider> Chats { get; } = [];

    public MessagingOptions Messaging { get; } = new();

    public FakeOutbox Outbox { get; } = new();

    public JobActivity Activity { get; } = new();

    public TestContext()
    {
        Jobs = new InMemoryJobs(Clock);
        Conversations = new FakeConversations(id => Jobs.TryGet(id)?.WorkItemId);
    }

    /// <summary>Acting as a work item's Assigned To in Azure DevOps (null: always agentd's own, as before).</summary>
    public Application.AzureDevOps.AdoOnBehalf? OnBehalf { get; set; }

    public Application.AzureDevOps.AdoActor? Actor { get; set; }

    public ClaimWorkItemHandler Claim() => new(WorkItems, Registry, Jobs, Clock, Notices, Options, OnBehalf, Actor);

    public StartNextJobHandler StartNext() => new(Jobs, Registry, Worktrees, WorkItems, MessagingService(), Outbox, Options);

    public MessagingService MessagingService()
    {
        var options = Microsoft.Extensions.Options.Options.Create(Messaging);
        return new(new MessagingProviderRegistry(Chats, options), new ConversationTargetsResolver(options), Conversations, Events, Clock);
    }

    public HandleAgentExitHandler AgentExit() => new(Jobs, Registry, Worktrees, Clock, Options);

    public PublishPullRequestHandler Publish() => new(Jobs, Registry, Worktrees, PullRequests, WorkItems, Outbox, Activity, Clock, Options, StartHandoff(), OnBehalf, Actor);

    public FinishWorkHandler Finish() => new(Jobs, Publish());

    public CancelJobHandler Cancel() => new(Jobs, Runner);

    public StartHandoffHandler StartHandoff() => new(Jobs, Registry, Worktrees, Options);

    public RequestCloseOutHandler RequestCloseOut() => new(Jobs);

    public AnswerCloseOutHandler AnswerCloseOut() =>
        new(Jobs, Conversations, new MessagingProviderRegistry(Chats, Microsoft.Extensions.Options.Options.Create(Messaging)), Outbox, Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnswerCloseOutHandler>.Instance);

    public ReviewPullRequestsHandler Review() => new(Jobs, Registry, PullRequests, Worktrees, Outbox, StartHandoff(), RequestCloseOut(), Options);

    public RecoverJobsOnStartupHandler Recover() => new(Jobs, Registry, Worktrees, Runner, Clock, Publish());

    public RetryDuePublishesHandler RetryPublishes() => new(Jobs, Clock, Publish());

    /// <summary>A job whose first push failed: Publishing, with a retry scheduled one minute ahead.</summary>
    public async Task<AgentRunRequest> PublishFailedJobAsync(int id = 1234)
    {
        var request = await RunningJobAsync(id);
        Worktrees.FailPushes = 1;
        _ = await Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), CancellationToken.None);
        return request;
    }

    /// <summary>Claims work item <paramref name="id"/> and starts it, returning the agent request.</summary>
    public async Task<AgentRunRequest> RunningJobAsync(int id = 1234)
    {
        WorkItems.Add(id);
        _ = await Claim().Handle(new ClaimWorkItem(WorkItemId.From(id)), CancellationToken.None);
        return (await StartNext().Handle(new StartNextJob("w1"), CancellationToken.None)).Value!;
    }
}
