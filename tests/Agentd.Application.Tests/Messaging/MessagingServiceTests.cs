using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.Events;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Messaging;

[TestClass]
public sealed class MessagingServiceTests
{
    [TestMethod]
    public async Task Starting_a_job_opens_a_conversation_per_enabled_provider()
    {
        var t = WithChats("discord", "slack");

        var request = await t.RunningJobAsync();

        foreach (var chat in t.Chats.Cast<FakeChat>())
        {
            var spec = chat.Opened.Single();
            Assert.AreEqual(request.JobId, spec.JobId);
            StringAssert.Contains(spec.Opening.Markdown, "#1234 Fix login");
        }

        var conversations = t.Conversations.All;
        Assert.HasCount(2, conversations);
        Assert.AreEqual(new Uri($"https://chat.example/thread-{request.JobId}"), conversations[0].Link);
    }

    [TestMethod]
    public async Task A_provider_failure_never_fails_the_job()
    {
        var t = WithChats("discord");
        ((FakeChat)t.Chats[0]).FailOpen = true;

        var request = await t.RunningJobAsync();

        Assert.AreEqual(JobState.Running, t.Jobs.Get(request.JobId).State);
        Assert.IsEmpty(t.Conversations.All);
        var degraded = t.Events.Appended.Single(e => e.Type == "MessagingDegraded");
        StringAssert.Contains(degraded.Payload, "gateway unavailable");
    }

    [TestMethod]
    public async Task Chat_tags_for_unknown_providers_are_recorded()
    {
        var t = WithChats("discord");
        t.WorkItems.Add(77, tags: ["ai-workflow", "repo:sysmin", "chat:telegram"]);
        await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(77)), default);

        await t.StartNext().Handle(new StartNextJob("w1"), default);

        StringAssert.Contains(t.Events.Appended.Single(e => e.Type == "MessagingTagIgnored").Payload, "chat:telegram");
        Assert.HasCount(1, t.Conversations.All, "falls back to the enabled provider");
    }

    [TestMethod]
    public async Task A_resumed_job_does_not_open_new_conversations()
    {
        var t = WithChats("discord");
        var request = await t.RunningJobAsync();
        var job = t.Jobs.Get(request.JobId);
        job.Defer(t.Clock.UtcNow, "usage limit");
        await t.Jobs.SaveAsync(job, default);

        var resumed = (await t.StartNext().Handle(new StartNextJob("w1"), default)).Value!;

        Assert.IsTrue(resumed.Resume);
        Assert.HasCount(1, ((FakeChat)t.Chats[0]).Opened);
    }

    [TestMethod]
    public async Task A_rerun_of_the_same_work_item_reuses_its_thread_and_announces_the_steps()
    {
        var t = WithChats("discord");
        var first = await t.RunningJobAsync();
        var job = t.Jobs.Get(first.JobId);
        job.Fail("boom");
        await t.Jobs.SaveAsync(job, default);
        await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(1234), Force: true), default);

        var second = (await t.StartNext().Handle(new StartNextJob("w1"), default)).Value!;

        Assert.HasCount(1, ((FakeChat)t.Chats[0]).Opened, "no second thread");
        var thread = t.Conversations.All.Single();
        Assert.AreEqual(second.JobId, thread.JobId, "the work item's thread moved to the new job");
        var started = t.Outbox.Enqueued.Where(e => e.Job == second.JobId).Select(e => e.Message.Message.Markdown).Single();
        StringAssert.Contains(started, "📄 **Work item #1234 fetched:** Fix login (Active; 1 acceptance criteria line(s))");
        StringAssert.Contains(started, "🌿 **Worktree ready:** `ai/1234-fix-login` from `develop`");
        StringAssert.Contains(started, "🤖 **Agent started**, session");
    }

    [TestMethod]
    public void Lifecycle_events_map_to_messages()
    {
        var at = DateTimeOffset.UnixEpoch;

        Assert.AreEqual(MessageKind.Error, JobEventMessages.For(new JobFailed("boom", JobState.Running, at))!.Message.Kind);
        StringAssert.Contains(JobEventMessages.For(new PullRequestCreated(new PullRequestUrl(new Uri("https://x/pullrequest/9")), at))!.Message.Markdown, "pullrequest/9");
        StringAssert.Contains(JobEventMessages.For(new JobCancelled("tngo", JobState.Running, at))!.Message.Markdown, "tngo");
        var question = JobEventMessages.For(new DeveloperQuestionAsked("Which?", ["v1", "v2"], at))!.Message;
        CollectionAssert.AreEqual(new[] { "opt1", "opt2" }, question.Options!.Select(o => o.Id).ToArray());
        Assert.IsNull(JobEventMessages.For(new JobStarted(BranchName.From("ai/1-x"), new WorktreePath("/w"), ClaudeSessionId.New(), at)), "the starter is sent when the conversation opens");
        Assert.IsNull(JobEventMessages.For(new DeveloperReplied("ok", "tngo", true, at)), "replies are expanded by For(events): a mirror plus an acknowledgement");
    }

    private static TestContext WithChats(params string[] keys)
    {
        var t = new TestContext();
        foreach (var key in keys)
        {
            t.Chats.Add(new FakeChat(key));
            t.Messaging.Providers[key] = new MessagingProviderSettings { Enabled = true };
        }

        return t;
    }
}
