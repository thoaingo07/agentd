using Agentd.Application.Jobs;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class StepAnnouncerTests
{
    private readonly TestContext _t = new();

    [TestMethod]
    [DataRow("plan", null, null, "claude-opus-5-5", "high", "📝 Plan · Claude · claude-opus-5-5 · effort high")]
    [DataRow("implement", null, "deepseek", "deepseek-flash", null, "🔨 Implement · deepseek · deepseek-flash")]
    [DataRow("fix", 2, "deepseek", "deepseek-flash", " ", "🔧 Fix round 2 · deepseek · deepseek-flash")]
    [DataRow("handoff", null, null, null, null, "🎓 Hand-off · Claude · default model")]
    public void The_line_names_the_step_provider_model_and_effort(string step, int? round, string? profile, string? model, string? effort, string expected) =>
        Assert.AreEqual(expected, StepAnnouncer.Line(step, round, profile, model, effort));

    [TestMethod]
    public async Task A_step_is_announced_once_in_the_thread_and_the_timeline_not_on_every_resume()
    {
        var turn = await _t.RunningJobAsync() with { Step = JobSteps.Implement, Profile = "deepseek" };
        var announcer = new StepAnnouncer(_t.Jobs, _t.Outbox, _t.Events);
        var before = _t.Outbox.Enqueued.Count;

        await announcer.AnnounceAsync(turn, "deepseek-flash", default);
        await announcer.AnnounceAsync(turn with { Resume = true }, "deepseek-flash", default);   // the same step resuming (an answer)
        await announcer.AnnounceAsync(turn with { Step = JobSteps.Handoff, Profile = null, Resume = true }, "claude-opus-5-5", default);

        CollectionAssert.AreEqual(new[] { "🔨 Implement · deepseek · deepseek-flash", "🎓 Hand-off · Claude · claude-opus-5-5" },
            _t.Outbox.Enqueued.Skip(before).Select(e => e.Message.Message.Markdown).ToList());
        var logged = _t.Events.Appended.Where(e => e.Type == StepAnnouncer.EventType).ToList();
        Assert.HasCount(2, logged);
        var payload = System.Text.Json.JsonDocument.Parse(logged[0].Payload).RootElement;
        Assert.AreEqual(("implement", "deepseek", "deepseek-flash", "🔨 Implement · deepseek · deepseek-flash"),
            (payload.GetProperty("step").GetString(), payload.GetProperty("profile").GetString(), payload.GetProperty("model").GetString(), payload.GetProperty("line").GetString()));
    }

    [TestMethod]
    public async Task A_run_that_never_started_shows_the_configured_model_of_its_profile()
    {
        var turn = await _t.RunningJobAsync() with { Step = JobSteps.Implement, Profile = "deepseek" };
        var models = new ModelsOptions();
        models.Profiles["deepseek"] = new ModelProfile { BaseUrl = "https://api.deepseek.com/anthropic", Model = "deepseek-flash", ApiKey = "k" };
        var before = _t.Outbox.Enqueued.Count;

        await new StepAnnouncer(_t.Jobs, _t.Outbox, _t.Events, Microsoft.Extensions.Options.Options.Create(models)).AnnounceAsync(turn, null, default);

        Assert.AreEqual("🔨 Implement · deepseek · deepseek-flash", _t.Outbox.Enqueued.Skip(before).Single().Message.Message.Markdown);
    }
}
