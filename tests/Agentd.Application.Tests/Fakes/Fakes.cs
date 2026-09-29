using System.Collections.Concurrent;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
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
        if (!_rows.TryGetValue(job.Id.Value, out var current) || current.Version != job.Version)
        {
            return Task.FromResult(Result.Fail(DomainError.Conflict("version changed")));
        }

        job.Persisted(job.Id, job.Version + 1);
        Store(job);
        return Task.FromResult(Result.Ok);
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

    public Job Get(JobId id) => Job.Rehydrate(_rows[id.Value], clock);

    private void Store(Job job)
    {
        SavedEventTypes.AddRange(job.DequeueEvents().Select(e => e.GetType().Name));
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

    public Task AddCommentAsync(int id, string text, CancellationToken cancellationToken)
    {
        Comments.Add((id, text));
        return Task.CompletedTask;
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

    public Task<bool> HasCommitsAheadAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken) => Task.FromResult(HasCommits);

    public Task PushAsync(WorktreePath worktree, BranchName branch, CancellationToken cancellationToken)
    {
        Pushed.Add(branch.Value);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PruneAsync(Repository repository, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakePullRequests : IPullRequestService
{
    public PullRequestRef? Existing { get; set; }

    public List<(string Source, string Target, string Title)> Created { get; } = [];

    public Task<PullRequestRef?> FindOpenAsync(Repository repository, BranchName source, CancellationToken cancellationToken) => Task.FromResult(Existing);

    public Task<PullRequestRef> CreateAsync(Repository repository, BranchName source, string target, string title, string description, WorkItemId workItem, CancellationToken cancellationToken)
    {
        Created.Add((source.Value, target, title));
        return Task.FromResult(new PullRequestRef(77, new Uri("https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/77")));
    }
}

internal sealed class FakeRunner : IAgentRunner
{
    public HashSet<long> Running { get; } = [];

    public List<long> CancelledJobs { get; } = [];

    public Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<AgentRunOutcome>(new AgentRunOutcome.Exited(0, null));

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

    public IOptions<JobOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new JobOptions());

    public NoMatchNotices Notices { get; } = new();

    public TestContext() => Jobs = new InMemoryJobs(Clock);

    public ClaimWorkItemHandler Claim() => new(WorkItems, Registry, Jobs, Clock, Notices, Options);

    public StartNextJobHandler StartNext() => new(Jobs, Registry, Worktrees, WorkItems, Options);

    public HandleAgentExitHandler AgentExit() => new(Jobs, Clock, Options);

    public PublishPullRequestHandler Publish() => new(Jobs, Registry, Worktrees, PullRequests, WorkItems);

    public FinishWorkHandler Finish() => new(Jobs, Publish());

    public CancelJobHandler Cancel() => new(Jobs, Runner);

    public RecoverJobsOnStartupHandler Recover() => new(Jobs, Runner, Publish());

    /// <summary>Claims work item <paramref name="id"/> and starts it, returning the agent request.</summary>
    public async Task<AgentRunRequest> RunningJobAsync(int id = 1234)
    {
        WorkItems.Add(id);
        _ = await Claim().Handle(new ClaimWorkItem(WorkItemId.From(id)), CancellationToken.None);
        return (await StartNext().Handle(new StartNextJob("w1"), CancellationToken.None)).Value!;
    }
}
