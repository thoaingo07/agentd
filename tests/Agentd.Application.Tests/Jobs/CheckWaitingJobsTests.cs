using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Domain.Jobs;
using Agentd.Domain.Messaging;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class CheckWaitingJobsTests
{
    [TestMethod]
    public async Task Reminds_at_half_and_ninety_percent_then_fails()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        var job = t.Jobs.Get(request.JobId);
        job.AskDeveloper("v1 or v2?");
        await t.Jobs.SaveAsync(job, default);
        var start = t.Clock.UtcNow;
        var check = new CheckWaitingJobsHandler(t.Jobs, t.Clock, t.Options);
        var timeout = t.Options.Value.WaitForHumanTimeout;

        async Task<int> At(double fraction)
        {
            t.Clock.UtcNow = start + timeout * fraction;
            return (await check.Handle(new CheckWaitingJobs(), default)).Value;
        }

        Assert.AreEqual(0, await At(0.4));
        Assert.AreEqual(1, await At(0.5));
        Assert.AreEqual(0, await At(0.6), "each reminder once");
        Assert.AreEqual(1, await At(0.9));
        Assert.AreEqual(2, t.Jobs.Get(request.JobId).WaitReminders);
        Assert.AreEqual(JobState.WaitingForHuman, t.Jobs.Get(request.JobId).State);

        Assert.AreEqual(1, await At(1.0));
        var failed = t.Jobs.Get(request.JobId);
        Assert.AreEqual(JobState.Failed, failed.State);
        StringAssert.Contains(failed.LastError, "No answer from the developer within 72 hours");

        var reminders = t.Jobs.SavedEvents.OfType<WaitReminderSent>().ToList();
        CollectionAssert.AreEqual(new[] { 1, 2 }, reminders.Select(r => r.Reminder).ToArray());
        Assert.AreEqual(start + timeout, reminders[0].ExpiresAt);
        StringAssert.Contains(JobEventMessages.For(reminders[0])!.Message.Markdown, "still waiting for your answer");
    }

    [TestMethod]
    public async Task A_reply_resets_the_wait()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        var job = t.Jobs.Get(request.JobId);
        job.AskDeveloper("first?");
        job.RemindWaiting(1, t.Clock.UtcNow);
        job.ResumeWith("yes", "tngo");

        Assert.IsNull(job.WaitingSince);
        Assert.AreEqual(0, job.WaitReminders);
        job.AskDeveloper("second?");
        Assert.AreEqual(t.Clock.UtcNow, job.WaitingSince);
        Assert.IsTrue(job.RemindWaiting(1, t.Clock.UtcNow).IsSuccess, "a new wait gets its own reminders");
    }
}
