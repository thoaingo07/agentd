using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Tests.Fakes;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Messaging;

[TestClass]
public sealed class HeartbeatTests
{
    private readonly TestContext _t = new();
    private readonly JobActivity _activity = new();
    private readonly HeartbeatState _state = new();
    private readonly FakeChat _chat = new("discord");

    public HeartbeatTests()
    {
        _t.Chats.Add(_chat);
        _t.Messaging.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
    }

    [TestMethod]
    public async Task Each_beat_posts_a_timestamped_status_and_deletes_the_previous_one()
    {
        var request = await _t.RunningJobAsync();
        _activity.SetPhase(request.JobId, "implement");
        _activity.RecordActivity(request.JobId, "🔧 dotnet test", _t.Clock.UtcNow);

        Assert.AreEqual(1, (await Beat()).Value);
        _t.Clock.UtcNow = _t.Clock.UtcNow.AddMinutes(1);
        await Beat();

        var beats = _chat.SentText.Where(t => t.Contains(" UTC", StringComparison.Ordinal)).ToList();
        Assert.HasCount(2, beats);
        StringAssert.Contains(beats[0], "🟢 running · implement · 🔧 dotnet test (0 s ago)");
        StringAssert.EndsWith(beats[1], "10:01:00 UTC");
        Assert.HasCount(1, _chat.Deleted, "the first heartbeat was removed when the second was posted");
        Assert.AreEqual(_t.Conversations.All.Single().StatusMessageId, $"m{_chat.SentText.Count}", "the newest heartbeat is remembered");
    }

    [TestMethod]
    public async Task A_waiting_job_shows_since_when()
    {
        var request = await _t.RunningJobAsync();
        var job = _t.Jobs.Get(request.JobId);
        job.AskDeveloper("v1 or v2?");
        await _t.Jobs.SaveAsync(job, default);

        await Beat();

        StringAssert.Contains(_chat.SentText[^1], "⏸ waiting for you since 10:00 UTC");
    }

    [TestMethod]
    public async Task Usage_warnings_fire_once_at_80_and_again_at_95_percent()
    {
        var request = await _t.RunningJobAsync();
        _activity.RecordUsage(request.JobId, new UsageSnapshot(0.82, 0.4, new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.Zero)));

        await Beat();
        await Beat();
        _activity.RecordUsage(request.JobId, new UsageSnapshot(0.96, 0.81, null));
        await Beat();

        var warnings = _t.Outbox.Enqueued.Select(e => e.Message.Message.Markdown).Where(m => m.StartsWith("⚠️ **Usage", StringComparison.Ordinal)).ToList();
        CollectionAssert.AreEqual(new[]
        {
            "⚠️ **Usage at 82% of the 5-hour window**, resets at 14:00 UTC.",
            "⚠️ **Usage at 96% of the 5-hour window**.",
            "⚠️ **Usage at 81% of the weekly window**.",
        }, warnings);
    }

    [TestMethod]
    public async Task A_silent_agent_is_flagged_once_and_cleared_when_it_resumes()
    {
        var request = await _t.RunningJobAsync();
        _activity.RecordActivity(request.JobId, "📖 reading README.md", _t.Clock.UtcNow);
        _t.Clock.UtcNow = _t.Clock.UtcNow.AddMinutes(6);

        await Beat();
        await Beat();
        _activity.RecordActivity(request.JobId, "✏️ editing README.md", _t.Clock.UtcNow);
        await Beat();

        var notes = _t.Outbox.Enqueued.Select(e => e.Message.Message.Markdown).Where(m => m.StartsWith("⚠️", StringComparison.Ordinal) || m.StartsWith('✅')).ToList();
        CollectionAssert.AreEqual(new[] { "⚠️ **No activity for 6 min** (last: 📖 reading README.md).", "✅ **Active again.**" }, notes);
    }

    [TestMethod]
    public async Task When_a_job_ends_its_last_heartbeat_is_removed()
    {
        var request = await _t.RunningJobAsync();
        await Beat();
        var job = _t.Jobs.Get(request.JobId);
        job.Cancel("tngo");
        await _t.Jobs.SaveAsync(job, default);

        await Beat();

        Assert.HasCount(1, _chat.Deleted);
        Assert.IsNull(_t.Conversations.All.Single().StatusMessageId);
    }

    private Task<Domain.Common.Result<int>> Beat() =>
        new PostHeartbeatsHandler(_t.Jobs, _t.Conversations, new MessagingProviderRegistry(_t.Chats, Microsoft.Extensions.Options.Options.Create(_t.Messaging)),
            _t.Outbox, _activity, _state, _t.Clock, _t.Options).Handle(new PostHeartbeats(), default);
}

[TestClass]
public sealed class SetPhaseTests
{
    [TestMethod]
    public async Task A_phase_is_posted_and_shown_in_status()
    {
        var t = new TestContext();
        var activity = new JobActivity();
        var handler = new SetPhaseHandler(activity, t.Outbox, t.Events);

        Assert.IsTrue((await handler.Handle(new SetPhase(new Domain.Jobs.ValueObjects.JobId(1), "Plan", "1. Edit AGENTS.md\n\nEstimate: ~10 min, ~5% of the 5-hour window"), default)).IsSuccess);
        Assert.AreEqual("validation", (await handler.Handle(new SetPhase(new Domain.Jobs.ValueObjects.JobId(1), "deploy", "x"), default)).Error?.Code);

        Assert.AreEqual("plan", activity.Get(new Domain.Jobs.ValueObjects.JobId(1)).Phase);
        StringAssert.StartsWith(t.Outbox.Enqueued.Single().Message.Message.Markdown, "📝 **Plan**\n\n1. Edit AGENTS.md");
        Assert.AreEqual("phase.set", t.Events.Appended.Single().Type);
    }
}
