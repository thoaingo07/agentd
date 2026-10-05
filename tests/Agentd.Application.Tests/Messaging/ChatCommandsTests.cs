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
public sealed class ChatCommandsTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private static readonly AgentdUser s_user = new(new UserId(1), "tngo", ["Admin"], true);
    private readonly TestContext _t = new();
    private readonly FakeOutbox _outbox = new();
    private readonly FakeTranscripts _transcripts = new();
    private readonly FakeChat _chat = new("discord");

    [TestMethod]
    public async Task Status_replies_in_the_thread_on_the_same_provider()
    {
        var conversation = await JobThreadAsync();

        var outcome = await Run("status", conversation);

        Assert.AreEqual(new InboundOutcome("command:status", conversation.JobId), outcome);
        var reply = _outbox.Enqueued.Single().Message;
        StringAssert.Contains(reply.Message.Markdown, "State: **Running**");
        CollectionAssert.AreEqual(new[] { s_discord }, reply.Options!.OnlyProviders!.ToArray());
    }

    [TestMethod]
    public async Task Cancel_cancels_as_the_user_and_lets_the_job_event_announce_it()
    {
        var conversation = await JobThreadAsync();

        await Run("cancel", conversation);

        Assert.AreEqual(JobState.Cancelled, _t.Jobs.Get(conversation.JobId).State);
        Assert.AreEqual("tngo", _t.Jobs.SavedEvents.OfType<Domain.Jobs.Events.JobCancelled>().Single().By);
        Assert.IsEmpty(_outbox.Enqueued, "the JobCancelled event posts the message");
    }

    [TestMethod]
    public async Task Retry_only_works_on_failed_jobs()
    {
        var conversation = await JobThreadAsync();

        await Run("retry", conversation);
        StringAssert.Contains(_outbox.Enqueued[^1].Message.Message.Markdown, "Can't retry");

        var job = _t.Jobs.Get(conversation.JobId);
        job.Fail("boom");
        await _t.Jobs.SaveAsync(job, default);
        await Run("retry", conversation);

        Assert.AreEqual(JobState.Queued, _t.Jobs.Get(conversation.JobId).State);
        StringAssert.Contains(_outbox.Enqueued[^1].Message.Message.Markdown, "attempt 2");
    }

    [TestMethod]
    public async Task Logs_attach_the_transcript_tail()
    {
        var conversation = await JobThreadAsync();
        _transcripts.Tail = "{\"type\":\"assistant\"}";

        await Run("logs", conversation);

        var attachment = _outbox.Enqueued.Single().Message.Message.Attachments!.Single();
        Assert.AreEqual("transcript-tail.jsonl", attachment.FileName);
        Assert.AreEqual(1234, _transcripts.AskedFor?.Value);
    }

    [TestMethod]
    public async Task List_and_run_work_outside_a_thread_and_reply_directly()
    {
        var conversation = await JobThreadAsync();
        _t.WorkItems.Add(555);

        await Run("list", null);
        await Run("run", null, "#555");
        await Run("run", null, "nope");

        Assert.IsEmpty(_outbox.Enqueued, "no job, so no outbox");
        StringAssert.Contains(_chat.SentText[0], "#1234 `sysmin`: Running");
        StringAssert.Contains(_chat.SentText[0], $"https://chat.example/thread-{conversation.JobId}");
        StringAssert.Contains(_chat.SentText[1], "for work item #555");
        StringAssert.Contains(_chat.SentText[2], "Usage: `run <work item id>`");
        Assert.IsNotNull(await _t.Jobs.FindActiveByWorkItemAsync(WorkItemId.From(555), default));
    }

    [TestMethod]
    public async Task Thread_commands_outside_a_thread_and_unknown_commands_get_help()
    {
        Assert.AreEqual("command:status", (await Run("status", null)).Code);
        StringAssert.Contains(_chat.SentText[0], "works in a job's thread");

        Assert.AreEqual("command:unknown", (await Run("deploy", null)).Code);
        StringAssert.Contains(_chat.SentText[1], "Unknown command `deploy`");
        StringAssert.Contains(_chat.SentText[1], "`list`");
        Assert.IsLessThan(300, _chat.SentText[1].Length, "an unknown command gets the short list");
    }

    [TestMethod]
    public async Task Help_lists_every_command_and_feature()
    {
        Assert.AreEqual("command:help", (await Run("help", null)).Code);

        var help = _chat.SentText.Single();
        foreach (var command in new[] { "status", "logs", "cancel", "retry", "handoff", "list", "run <work item id>", "help" })
        {
            StringAssert.Contains(help, $"`{command}`");
        }

        foreach (var feature in new[] { "plan", "pull request", "Review loop", "hand-off", "Close-out", "heartbeat", "80%", "ai-auto", "progress" })
        {
            StringAssert.Contains(help, feature);
        }

        Assert.IsLessThan(2000, help.Length, "fits in one Discord message");
    }

    [TestMethod]
    public async Task Repair_reopens_missing_conversations_for_running_jobs()
    {
        _t.Chats.Add(_chat);
        _t.Messaging.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        _chat.FailOpen = true;
        var request = await _t.RunningJobAsync();
        Assert.IsEmpty(_t.Conversations.All);
        _chat.FailOpen = false;
        var repair = new RepairConversationsHandler(_t.Jobs, _t.Conversations, _t.WorkItems, Registry(), _t.MessagingService());

        Assert.AreEqual(1, (await repair.Handle(new RepairConversations(), default)).Value);
        Assert.AreEqual(request.JobId, _t.Conversations.All.Single().JobId);
        Assert.AreEqual(0, (await repair.Handle(new RepairConversations(), default)).Value, "nothing missing any more");
    }

    internal static ChatCommands Commands(TestContext t, FakeOutbox outbox, ITranscriptReader transcripts, IMessagingProvider? chat = null)
    {
        var options = new MessagingOptions();
        options.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        var registry = new MessagingProviderRegistry(chat is null ? [] : [chat], Microsoft.Extensions.Options.Options.Create(options));
        return new ChatCommands(t.Jobs, new GetJobStatusHandler(t.Jobs, t.Clock), t.Cancel(), new RetryJobHandler(t.Jobs), t.Claim(), t.StartHandoff(),
            t.Conversations, transcripts, outbox, registry, NullLogger<ChatCommands>.Instance);
    }

    private MessagingProviderRegistry Registry() =>
        new(_t.Chats, Microsoft.Extensions.Options.Options.Create(_t.Messaging));

    private Task<InboundOutcome> Run(string name, Conversation? conversation, params string[] args) =>
        Commands(_t, _outbox, _transcripts, _chat).ExecuteAsync(
            new InboundMessage(s_discord, Guid.NewGuid().ToString(), conversation?.ExternalConversationId ?? "channel-1", "789", "Thoai", null, new InboundCommand(name, args), null, DateTimeOffset.UtcNow),
            s_user, conversation, default);

    private async Task<Conversation> JobThreadAsync()
    {
        var request = await _t.RunningJobAsync();
        var conversation = Conversation.Open(request.JobId, s_discord, "thread-1", null, new Uri($"https://chat.example/thread-{request.JobId}"), [], _t.Clock.UtcNow).Value!;
        await _t.Conversations.AddAsync(conversation, default);
        _t.Jobs.SavedEvents.Clear();
        return conversation;
    }
}

internal sealed class FakeTranscripts : ITranscriptReader
{
    public string? Tail { get; set; }

    public WorkItemId? AskedFor { get; private set; }

    public Task<string?> TailAsync(WorkItemId workItem, int lines, CancellationToken cancellationToken)
    {
        AskedFor = workItem;
        return Task.FromResult(Tail);
    }
}
