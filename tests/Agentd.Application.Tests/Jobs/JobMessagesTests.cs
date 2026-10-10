using Agentd.Application.Ideas;
using Agentd.Application.Jobs;
using Agentd.Domain.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

/// <summary>A message to a job routes the same from chat and the Web UI (no provider).</summary>
[TestClass]
public sealed class JobMessagesTests
{
    private readonly TestContext _t = new();
    private readonly Agent _agent = new();

    [TestMethod]
    public async Task In_review_a_message_starts_a_fix_round()
    {
        _t.Options.Value.ReviewLoop = true;
        var request = await _t.RunningJobAsync();
        await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default);
        using var followUps = FollowUps();

        var outcome = await Messages(followUps).RouteAsync(request.JobId, "rename it to SyncJob", "tngo", null, default);

        Assert.AreEqual(JobMessageOutcomes.FixRound, outcome.Value);
        Assert.AreEqual(JobState.Running, _t.Jobs.Get(request.JobId).State);
        Assert.IsEmpty(_agent.Turns);
    }

    [TestMethod]
    public async Task After_the_merge_a_message_is_a_follow_up_answered_by_the_jobs_session()
    {
        _t.Options.Value.ReviewLoop = true;
        var request = await _t.RunningJobAsync();
        await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default);
        _t.PullRequests.Status = Ports.PullRequestStatus.Completed;
        await _t.Review().Handle(new ReviewPullRequests(), default);
        using var followUps = FollowUps();

        var outcome = await Messages(followUps).RouteAsync(request.JobId, "when does the sync run?", "tngo", null, default);
        await _agent.Answered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(JobMessageOutcomes.FollowUp, outcome.Value);
        Assert.AreEqual(JobState.Done, _t.Jobs.Get(request.JobId).State);
        StringAssert.Contains(_agent.Turns.Single().Prompt, "tngo writes in the thread:\n\nwhen does the sync run?");
    }

    [TestMethod]
    public async Task A_cancelled_job_refuses_and_a_missing_one_is_not_found()
    {
        var request = await _t.RunningJobAsync();
        _t.Jobs.ForceState(request.JobId, JobState.Cancelled);
        using var followUps = FollowUps();

        var refused = await Messages(followUps).RouteAsync(request.JobId, "hello?", "tngo", null, default);
        var missing = await Messages(followUps).RouteAsync(new Domain.Jobs.ValueObjects.JobId(999), "hello?", "tngo", null, default);

        Assert.AreEqual(JobMessageOutcomes.NotAccepted, refused.Value);
        Assert.AreEqual("not_found", missing.Error!.Code);
    }

    private JobFollowUps FollowUps() => new(_t.Registry, _t.Worktrees, _agent, _t.Outbox, NullLogger<JobFollowUps>.Instance);

    private JobMessages Messages(JobFollowUps followUps) =>
        new(new SubmitDeveloperMessageHandler(_t.Jobs), _t.AnswerCloseOut(), null, followUps, _t.Jobs);

    private sealed class Agent : IBrainstormAgent
    {
        public List<BrainstormTurn> Turns { get; } = [];

        public TaskCompletionSource Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            Turns.Add(turn);
            Answered.TrySetResult();
            return Task.FromResult(new BrainstormReply("Every night at 02:00.", null, null));
        }
    }
}
