using Agentd.Application.Ideas;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class SweepWorktreesTests
{
    [TestMethod]
    public async Task Only_checkouts_nothing_uses_are_removed_and_unknown_folders_stay()
    {
        var t = new TestContext();
        var start = t.Clock.UtcNow;
        var jobs = new Dictionary<int, JobId>();
        foreach (var wi in new[] { 1, 2, 3, 4, 5 })
        {
            jobs[wi] = (await t.RunningJobAsync(wi)).JobId;
        }

        await FailAsync(t, jobs[3], start - TimeSpan.FromDays(4));   // failed long ago: past the retry window
        await FailAsync(t, jobs[2], start - TimeSpan.FromDays(1));   // failed yesterday: kept for !retry
        t.Clock.UtcNow = start - TimeSpan.FromDays(2);
        await t.Finish().Handle(new FinishWork(jobs[4], "T", "D", "S"), default);   // done two days ago
        t.Clock.UtcNow = start - TimeSpan.FromHours(1);
        await t.Finish().Handle(new FinishWork(jobs[5], "T", "D", "S"), default);   // done an hour ago: follow-ups
        t.Clock.UtcNow = start;
        t.Worktrees.Folders.AddRange(["wi-1", "wi-2", "wi-3", "wi-4", "wi-5", "wi-6", "idea-1", "idea-2", "review-1", "review-2", "notes"]);
        var ideas = new Ideas(new() { [1] = IdeaStatus.Brainstorming, [2] = IdeaStatus.Created });
        var reviews = new Reviews(new() { [1] = ReviewStatus.Reviewed, [2] = ReviewStatus.Closed });
        var sweep = new SweepWorktreesHandler(t.Registry, t.Worktrees, t.Jobs, new History(t, jobs), t.Clock, t.Options, ideas, reviews);

        var removed = await sweep.Handle(new SweepWorktrees(), default);

        Assert.AreEqual(5, removed.Value);
        CollectionAssert.AreEquivalent(new[] { "wi-3", "wi-4", "wi-6", "idea-2", "review-2" }, t.Worktrees.Removed.Select(Path.GetFileName).ToArray(),
            "kept: the running job, a job failed yesterday, one done an hour ago, an open idea, an open review, and a folder agentd doesn't know");
        CollectionAssert.Contains(t.Worktrees.Pruned, "sysmin");
    }

    private static async Task FailAsync(TestContext t, JobId id, DateTimeOffset at)
    {
        t.Clock.UtcNow = at;
        var job = t.Jobs.Get(id);
        job.Fail("boom");
        await t.Jobs.SaveAsync(job, default);
    }

    private sealed class History(TestContext t, Dictionary<int, JobId> jobs) : IWorkItemHistory
    {
        public Task<IReadOnlyList<Job>> ListJobsAsync(WorkItemId workItem, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Job>>(jobs.TryGetValue(workItem.Value, out var id) ? [t.Jobs.Get(id)] : []);

        public Task<IReadOnlyList<Agentd.Application.Events.AgentEventDto>> ReadEventsAsync(WorkItemId workItem, long? after, long? before, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PostedMessage>> ListPostedAsync(WorkItemId workItem, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Ideas(Dictionary<long, string> statuses) : IIdeaStore
    {
        public Task<Idea?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(statuses.TryGetValue(id, out var s)
            ? new Idea(id, "sysmin", "t", "tngo", ProviderKey.From("discord"), "t", null, s, null, null, null, null, null, [])
            : null);

        public Task<long> InsertAsync(string repository, string title, string author, ProviderKey provider, string threadId, string? spaceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Idea?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveAsync(Idea idea, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddMessageAsync(long ideaId, string direction, string author, string text, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long ideaId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<IdeaSummary>> ListSummariesAsync(long? id, int? workItem, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Reviews(Dictionary<long, string> statuses) : IReviewStore
    {
        public Task<Review?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(statuses.TryGetValue(id, out var s)
            ? new Review(id, "sysmin", 1, "t", "tngo", ProviderKey.From("discord"), "t", null, s, null, null, null, null, null, null, null, [])
            : null);

        public Task<long> InsertAsync(string repository, int pullRequestId, string title, string author, ProviderKey provider, string threadId, string? spaceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Review?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveAsync(Review review, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddMessageAsync(long reviewId, string direction, string author, string text, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long reviewId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
