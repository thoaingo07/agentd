using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.Events;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

namespace Agentd.Domain.Tests.Jobs;

[TestClass]
public sealed class JobTransitionTests
{
    private static readonly WorktreePath s_worktree = new("/tmp/wt/wi-1234");
    private static readonly BranchName s_branch = BranchName.For(WorkItemId.From(1234), "Fix login");
    private static readonly ClaudeSessionId s_session = ClaudeSessionId.New();
    private static readonly PullRequestDraft s_draft = Result.Success(PullRequestDraft.Create("Fix login", "desc", "sum").Value!).Value!;
    private static readonly PullRequestUrl s_url = new(new Uri("https://dev.azure.com/org/proj/_git/repo/pullrequest/1"));

    /// <summary>Operations under test, and the (from → to, event) pairs that are allowed.</summary>
    private static readonly Dictionary<string, Func<Job, Result>> s_operations = new()
    {
        ["BeginPreparing"] = j => j.BeginPreparing(),
        ["Start"] = j => j.Start(s_worktree, s_branch, s_session),
        ["Finish"] = j => j.Finish(s_draft),
        ["Complete"] = j => j.Complete(s_url),
        ["Fail"] = j => j.Fail("boom"),
        ["Cancel"] = j => j.Cancel("tngo"),
        ["Retry"] = j => j.Retry(),
        ["MarkRecovered"] = j => j.MarkRecovered(),
        ["Defer"] = j => j.Defer(DateTimeOffset.UnixEpoch, "usage limit"),
        ["PublishFailed"] = j => j.PublishFailed("push failed", 3, DateTimeOffset.UnixEpoch),
        ["Requeue"] = j => j.Requeue("restarted"),
        ["AskDeveloper"] = j => j.AskDeveloper("Which endpoint?", ["v1", "v2"]),
        ["ResumeWith"] = j => j.ResumeWith("Use v2", "tngo"),
    };

    private static readonly Dictionary<(JobState From, string Op), (JobState To, Type Event)> s_allowed = new()
    {
        [(JobState.Queued, "BeginPreparing")] = (JobState.Preparing, typeof(JobPreparing)),
        [(JobState.Preparing, "Start")] = (JobState.Running, typeof(JobStarted)),
        [(JobState.Running, "Finish")] = (JobState.Publishing, typeof(JobFinished)),
        [(JobState.Publishing, "Complete")] = (JobState.Done, typeof(PullRequestCreated)),
        [(JobState.Running, "MarkRecovered")] = (JobState.Running, typeof(JobRecovered)),
        [(JobState.Running, "Defer")] = (JobState.Queued, typeof(JobDeferred)),
        [(JobState.Preparing, "Requeue")] = (JobState.Queued, typeof(JobRequeued)),
        [(JobState.Running, "AskDeveloper")] = (JobState.WaitingForHuman, typeof(DeveloperQuestionAsked)),
        [(JobState.WaitingForHuman, "ResumeWith")] = (JobState.Running, typeof(DeveloperReplied)),
        [(JobState.Running, "ResumeWith")] = (JobState.Running, typeof(DeveloperReplied)),
        [(JobState.WaitingForHuman, "Fail")] = (JobState.Failed, typeof(JobFailed)),
        [(JobState.WaitingForHuman, "Cancel")] = (JobState.Cancelled, typeof(JobCancelled)),
        [(JobState.Publishing, "PublishFailed")] = (JobState.Publishing, typeof(PublishRetryScheduled)),
        [(JobState.Failed, "Retry")] = (JobState.Queued, typeof(JobRetried)),
        [(JobState.Queued, "Fail")] = (JobState.Failed, typeof(JobFailed)),
        [(JobState.Preparing, "Fail")] = (JobState.Failed, typeof(JobFailed)),
        [(JobState.Running, "Fail")] = (JobState.Failed, typeof(JobFailed)),
        [(JobState.Publishing, "Fail")] = (JobState.Failed, typeof(JobFailed)),
        [(JobState.Queued, "Cancel")] = (JobState.Cancelled, typeof(JobCancelled)),
        [(JobState.Preparing, "Cancel")] = (JobState.Cancelled, typeof(JobCancelled)),
        [(JobState.Running, "Cancel")] = (JobState.Cancelled, typeof(JobCancelled)),
        [(JobState.Publishing, "Cancel")] = (JobState.Cancelled, typeof(JobCancelled)),
    };

    public static IEnumerable<object[]> EveryStateAndOperation =>
        from state in Enum.GetValues<JobState>()
        from op in s_operations.Keys
        select new object[] { state, op };

    [TestMethod]
    [DynamicData(nameof(EveryStateAndOperation))]
    public void Every_state_and_operation_behaves_as_specified(JobState from, string operation)
    {
        var job = JobIn(from);
        _ = job.DequeueEvents();

        var result = s_operations[operation](job);
        var events = job.DequeueEvents();

        if (s_allowed.TryGetValue((from, operation), out var expected))
        {
            Assert.IsTrue(result.IsSuccess, $"{operation} from {from} should succeed: {result.Error}");
            Assert.AreEqual(expected.To, job.State);
            Assert.HasCount(1, events);
            Assert.IsInstanceOfType(events[0], expected.Event);
        }
        else
        {
            Assert.IsFalse(result.IsSuccess, $"{operation} from {from} should be rejected");
            Assert.AreEqual("invalid_transition", result.Error!.Code);
            Assert.AreEqual(from, job.State, "state must not change on a rejected transition");
            Assert.IsEmpty(events);
        }
    }

    [TestMethod]
    public void Create_raises_JobQueued_at_attempt_one()
    {
        var job = Job.Create(WorkItemId.From(7), RepositoryName.From("agentd"), " Title ", FakeClock.Default());

        Assert.AreEqual(JobState.Queued, job.State);
        Assert.AreEqual(1, job.Attempt);
        Assert.AreEqual("Title", job.Title);
        Assert.IsInstanceOfType<JobQueued>(job.DequeueEvents().Single());
    }

    [TestMethod]
    public void Retry_increments_attempt_and_clears_last_error()
    {
        var job = JobIn(JobState.Failed);

        job.Retry();

        Assert.AreEqual(2, job.Attempt);
        Assert.IsNull(job.LastError);
    }

    [TestMethod]
    public void Defer_keeps_the_session_and_sets_NotBefore()
    {
        var job = JobIn(JobState.Running);
        var session = job.Session;
        var resetAt = new DateTimeOffset(2026, 9, 29, 14, 5, 0, TimeSpan.Zero);

        job.Defer(resetAt, "usage limit");

        Assert.AreEqual(JobState.Queued, job.State);
        Assert.AreEqual(session, job.Session);
        Assert.AreEqual(resetAt, job.NotBefore);
    }

    [TestMethod]
    public void A_reply_while_running_is_queued_for_the_next_turn()
    {
        var job = JobIn(JobState.Running);

        job.ResumeWith("also update the docs", "tngo");
        job.ResumeWith("and the changelog", "tngo");

        Assert.AreEqual(JobState.Running, job.State);
        CollectionAssert.AreEqual(new[] { "also update the docs", "and the changelog" }, job.PendingMessages.ToArray());
        Assert.IsFalse(job.DequeueEvents().OfType<DeveloperReplied>().Any(e => e.Resumed));
        Assert.HasCount(2, job.TakePendingMessages());
        Assert.IsEmpty(job.PendingMessages);
    }

    [TestMethod]
    public void A_reply_while_waiting_resumes_without_queuing()
    {
        var job = JobIn(JobState.WaitingForHuman);

        job.ResumeWith("v2", "tngo");

        Assert.AreEqual(JobState.Running, job.State);
        Assert.IsEmpty(job.PendingMessages);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Empty_questions_and_replies_are_rejected(string text)
    {
        var job = JobIn(JobState.Running);

        Assert.AreEqual("validation", job.AskDeveloper(text).Error?.Code);
        Assert.AreEqual("validation", job.ResumeWith(text, "tngo").Error?.Code);
        Assert.AreEqual(JobState.Running, job.State);
    }

    [TestMethod]
    public void MarkRecovered_counts_resumes()
    {
        var job = JobIn(JobState.Running);

        job.MarkRecovered();
        job.MarkRecovered();

        Assert.AreEqual(2, job.ResumeCount);
    }

    [TestMethod]
    public void Transitions_update_UpdatedAt_from_the_clock()
    {
        var clock = FakeClock.Default();
        var job = Job.Create(WorkItemId.From(7), RepositoryName.From("agentd"), "t", clock);
        clock.UtcNow = clock.UtcNow.AddMinutes(5);

        job.BeginPreparing();

        Assert.AreEqual(clock.UtcNow, job.UpdatedAt);
    }

    [TestMethod]
    public void Rehydrate_restores_state_without_raising_events()
    {
        var snapshot = new JobSnapshot(new JobId(42), WorkItemId.From(9), RepositoryName.From("r"), "t", JobState.Running,
            s_branch, s_worktree, s_session, null, null, 2, 1, 0, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 5);

        var job = Job.Rehydrate(snapshot, FakeClock.Default());

        Assert.AreEqual(new JobId(42), job.Id);
        Assert.AreEqual(JobState.Running, job.State);
        Assert.AreEqual(5L, job.Version);
        Assert.IsEmpty(job.DequeueEvents());
    }

    private static Job JobIn(JobState state)
    {
        var job = Job.Create(WorkItemId.From(1234), RepositoryName.From("agentd"), "Fix login", FakeClock.Default());
        switch (state)
        {
            case JobState.Queued:
                break;
            case JobState.Preparing:
                job.BeginPreparing();
                break;
            case JobState.Running:
                job.BeginPreparing();
                job.Start(s_worktree, s_branch, s_session);
                break;
            case JobState.WaitingForHuman:
                job.BeginPreparing();
                job.Start(s_worktree, s_branch, s_session);
                job.AskDeveloper("Which endpoint?");
                break;
            case JobState.Publishing:
                job.BeginPreparing();
                job.Start(s_worktree, s_branch, s_session);
                job.Finish(s_draft);
                break;
            case JobState.Done:
                job.BeginPreparing();
                job.Start(s_worktree, s_branch, s_session);
                job.Finish(s_draft);
                job.Complete(s_url);
                break;
            case JobState.Failed:
                job.Fail("boom");
                break;
            case JobState.Cancelled:
                job.Cancel("tngo");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        Assert.AreEqual(state, job.State);
        return job;
    }
}
