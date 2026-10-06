using Agentd.Application.Ideas;
using Agentd.Application.Jobs;
using Agentd.Domain.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class JobFollowUpsTests
{
    [TestMethod]
    public async Task After_the_merge_a_question_resumes_the_jobs_session_read_only_and_the_answer_goes_to_the_thread()
    {
        var t = new TestContext();
        t.Options.Value.ReviewLoop = true;
        var request = await t.RunningJobAsync();
        await t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default);
        t.PullRequests.Status = Ports.PullRequestStatus.Completed;
        await t.Review().Handle(new ReviewPullRequests(), default);
        var job = t.Jobs.Get(request.JobId);
        var agent = new Agent("It runs every night at 02:00 via the CronJob.");
        using var followUps = new JobFollowUps(t.Registry, t.Worktrees, agent, t.Outbox, NullLogger<JobFollowUps>.Instance);
        var posted = t.Outbox.Enqueued.Count;

        Assert.AreEqual(JobState.Done, job.State);
        Assert.IsTrue(JobFollowUps.Accepts(job));
        followUps.Ask(job, "tngo", "when does the sync run?");
        await agent.Answered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100 && t.Outbox.Enqueued.Count == posted; i++)
        {
            await Task.Delay(20);
        }

        var turn = agent.Turns.Single();
        Assert.AreEqual((ThreadTurnKind.FollowUp, true, request.Session.Value), (turn.Kind, turn.Resume, turn.Session), "the job's own session, resumed");
        StringAssert.Contains(turn.Prompt, "is merged");
        StringAssert.Contains(turn.Prompt, "when does the sync run?");
        CollectionAssert.Contains(t.Worktrees.Detached, $"wi-{job.WorkItemId}", "the removed worktree comes back at the same path, read-only");
        Assert.AreEqual("It runs every night at 02:00 via the CronJob.", t.Outbox.Enqueued[^1].Message.Message.Markdown);
        Assert.HasCount(1, t.PullRequests.Created, "talk only: nothing published");
    }

    private sealed class Agent(string answer) : IBrainstormAgent
    {
        public List<BrainstormTurn> Turns { get; } = [];

        public TaskCompletionSource Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            Turns.Add(turn);
            Answered.TrySetResult();
            return Task.FromResult(new BrainstormReply(answer, null, null));
        }
    }
}
