using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Tests.Fakes;
using Agentd.Application.Users;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Messaging;

[TestClass]
public sealed class InboundMessageHandlerTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private readonly TestContext _t = new();
    private readonly FakeLog _log = new();
    private readonly FakeUsers _users = new();
    private readonly FakeOutbox _outbox = new();
    private readonly JobActivity _activity = new();
    private int _nextMessage;

    [TestMethod]
    public async Task A_reply_resumes_a_waiting_job_and_is_mirrored_elsewhere()
    {
        var job = await JobInThreadAsync(waiting: true);

        var outcome = await Handler().ProcessAsync(Message("use v2"), default);

        Assert.AreEqual(new InboundOutcome("resumed", job), outcome);
        Assert.AreEqual(JobState.Running, _t.Jobs.Get(job).State);
        var replied = _t.Jobs.SavedEvents.OfType<DeveloperReplied>().Single();
        Assert.AreEqual(("tngo", s_discord), (replied.From, replied.Via));
        var mirror = JobEventMessages.For([replied]).Single();
        CollectionAssert.AreEqual(new[] { s_discord }, mirror.Options!.ExceptProviders!.ToArray());
        StringAssert.Contains(mirror.Message.Markdown, "use v2");
        Assert.AreEqual(("resumed", (long?)job.Value, (long?)1L), _log.Outcomes.Single());
    }

    [TestMethod]
    public async Task A_message_mid_task_gets_an_instant_status_and_is_queued_for_the_agent()
    {
        var job = await JobInThreadAsync(waiting: false);
        _activity.SetPhase(job, "implement");
        _activity.RecordActivity(job, "📖 reading AGENTS.md", _t.Clock.UtcNow.AddSeconds(-20));
        _activity.RecordUsage(job, new UsageSnapshot(0.62, 0.3, null));

        Assert.AreEqual("queued", (await Handler().ProcessAsync(Message("what's the progress?"), default)).Code);

        CollectionAssert.AreEqual(new[] { "tngo: what's the progress?" }, _t.Jobs.Get(job).PendingMessages.ToArray());
        var status = _outbox.Enqueued.Single().Message;
        CollectionAssert.AreEqual(new[] { s_discord }, status.Options!.OnlyProviders!.ToArray());
        StringAssert.Contains(status.Message.Markdown, "**Status:** 🟢 running · implement · 📖 reading AGENTS.md (20 s ago)");
        StringAssert.Contains(status.Message.Markdown, "usage 5h 62% / week 30%");
        StringAssert.Contains(status.Message.Markdown, "reaches the agent at its next step");
    }

    [TestMethod]
    public async Task A_finished_job_says_so_where_the_message_came_from()
    {
        var job = await JobInThreadAsync(waiting: false);
        var j = _t.Jobs.Get(job);
        j.Cancel("tngo");
        await _t.Jobs.SaveAsync(j, default);

        Assert.AreEqual("not_accepted", (await Handler().ProcessAsync(Message("hello?"), default)).Code);

        var reply = _outbox.Enqueued.Single();
        CollectionAssert.AreEqual(new[] { s_discord }, reply.Message.Options!.OnlyProviders!.ToArray());
    }

    [TestMethod]
    public async Task The_same_message_is_handled_once()
    {
        await JobInThreadAsync(waiting: false);
        var message = Message("once");

        await Handler().ProcessAsync(message, default);
        var second = await Handler().ProcessAsync(message, default);

        Assert.AreEqual("duplicate", second.Code);
        Assert.HasCount(1, _t.Jobs.Get(_t.Conversations.All[0].JobId).PendingMessages);
    }

    [TestMethod]
    public async Task Strangers_are_ignored_without_a_reply()
    {
        await JobInThreadAsync(waiting: true);

        var outcome = await Handler().ProcessAsync(Message("hi", userId: "stranger"), default);

        Assert.AreEqual("ignored_unknown_user", outcome.Code);
        Assert.AreEqual(JobState.WaitingForHuman, _t.Jobs.Get(_t.Conversations.All[0].JobId).State);
        Assert.IsEmpty(_outbox.Enqueued);
    }

    [TestMethod]
    public async Task With_allow_everyone_a_stranger_acts_under_their_display_name()
    {
        var job = await JobInThreadAsync(waiting: true);
        var open = new MessagingOptions();
        open.Providers["discord"] = new MessagingProviderSettings { Enabled = true, AllowEveryone = true };

        var outcome = await Handler(open).ProcessAsync(Message("use v2", userId: "stranger"), default);

        Assert.AreEqual("resumed", outcome.Code);
        Assert.AreEqual(JobState.Running, _t.Jobs.Get(job).State);
        Assert.AreEqual("Thoai", _t.Jobs.SavedEvents.OfType<DeveloperReplied>().Single().From, "their display name");
        Assert.AreEqual(("resumed", (long?)job.Value, (long?)null), _log.Outcomes.Single(), "a guest is not stored as a user");
    }

    [TestMethod]
    public async Task Allow_everyone_never_unblocks_a_listed_user_marked_inactive()
    {
        await JobInThreadAsync(waiting: true);
        var open = new MessagingOptions();
        open.Providers["discord"] = new MessagingProviderSettings { Enabled = true, AllowEveryone = true };

        var outcome = await Handler(open).ProcessAsync(Message("hi", userId: "inactive"), default);

        Assert.AreEqual("ignored_unknown_user", outcome.Code);
    }

    [TestMethod]
    public async Task Messages_outside_a_job_thread_are_ignored()
    {
        Assert.AreEqual("ignored_no_job", (await Handler().ProcessAsync(Message("hi"), default)).Code);
    }

    [TestMethod]
    public async Task A_failure_releases_the_message_for_redelivery()
    {
        await JobInThreadAsync(waiting: true);
        _users.Throw = true;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Handler().ProcessAsync(Message("x"), default));

        Assert.HasCount(1, _log.Forgotten);
    }

    [TestMethod]
    public async Task Racing_replies_resume_once_and_queue_the_rest()
    {
        for (var round = 0; round < 100; round++)
        {
            var t = new TestContext();
            var request = await t.RunningJobAsync(round + 1);
            var job = t.Jobs.Get(request.JobId);
            job.AskDeveloper("Which?");
            await t.Jobs.SaveAsync(job, default);
            var submit = new SubmitDeveloperMessageHandler(t.Jobs);

            var results = await Task.WhenAll(
                Task.Run(() => submit.Handle(new SubmitDeveloperMessage(request.JobId, "a", "alice"), default)),
                Task.Run(() => submit.Handle(new SubmitDeveloperMessage(request.JobId, "b", "bob"), default)));

            CollectionAssert.AreEquivalent(new[] { DeveloperMessageOutcome.Resumed, DeveloperMessageOutcome.Queued }, results.Select(r => r.Value).ToArray(), $"round {round}");
            Assert.HasCount(2, t.Jobs.Get(request.JobId).PendingMessages, "both replies reach the agent");
        }
    }

    private InboundMessageHandler Handler(MessagingOptions? messaging = null) =>
        new(_log, _users, _t.Conversations, new SubmitDeveloperMessageHandler(_t.Jobs), ChatCommandsTests.Commands(_t, _outbox, new FakeTranscripts()), _t.AnswerCloseOut(), _outbox, _t.Jobs, _activity, _t.Clock, NullLogger<InboundMessageHandler>.Instance,
            messaging is null ? null : new Monitor(messaging));

    private sealed class Monitor(MessagingOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<MessagingOptions>
    {
        public MessagingOptions CurrentValue => value;

        public MessagingOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<MessagingOptions, string?> listener) => null;
    }

    private async Task<JobId> JobInThreadAsync(bool waiting)
    {
        var request = await _t.RunningJobAsync();
        if (waiting)
        {
            var job = _t.Jobs.Get(request.JobId);
            job.AskDeveloper("Which endpoint?");
            await _t.Jobs.SaveAsync(job, default);
        }

        await _t.Conversations.AddAsync(Conversation.Open(request.JobId, s_discord, "thread-1", null, null, [], _t.Clock.UtcNow).Value!, default);
        _t.Jobs.SavedEvents.Clear();
        return request.JobId;
    }

    private InboundMessage Message(string text, string userId = "789") =>
        new(s_discord, $"msg-{++_nextMessage}", "thread-1", userId, "Thoai", text, null, null, DateTimeOffset.UtcNow);

    private sealed class FakeLog : IInboundLog
    {
        private readonly HashSet<string> _seen = [];

        public List<(string Outcome, long? Job, long? User)> Outcomes { get; } = [];

        public List<string> Forgotten { get; } = [];

        public Task<bool> TryRecordAsync(ProviderKey provider, string externalMessageId, DateTimeOffset receivedAt, CancellationToken cancellationToken) =>
            Task.FromResult(_seen.Add(externalMessageId));

        public Task SetOutcomeAsync(ProviderKey provider, string externalMessageId, string outcome, JobId? jobId, UserId? userId, CancellationToken cancellationToken)
        {
            Outcomes.Add((outcome, jobId?.Value, userId?.Value));
            return Task.CompletedTask;
        }

        public Task ForgetAsync(ProviderKey provider, string externalMessageId, CancellationToken cancellationToken)
        {
            _seen.Remove(externalMessageId);
            Forgotten.Add(externalMessageId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUsers : IUserDirectory
    {
        public bool Throw { get; set; }

        public Task<AgentdUser?> FindByIdentityAsync(ProviderKey provider, string externalId, CancellationToken cancellationToken) =>
            Throw
                ? throw new InvalidOperationException("db down")
                : Task.FromResult(externalId switch
                {
                    "789" => new AgentdUser(new UserId(1), "tngo", ["Admin"], true),
                    "inactive" => new AgentdUser(new UserId(2), "former", [], false),
                    _ => null,
                });

        public Task SyncAsync(IReadOnlyList<User> users, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
