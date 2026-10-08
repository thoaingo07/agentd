using Agentd.Application.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class ProfileSessionsTests
{
    private readonly TestContext _t = new();
    private readonly MemorySessions _sessions = new();
    private readonly MemoryPlans _plans = new();

    [TestMethod]
    public async Task Default_turns_keep_the_jobs_own_session()
    {
        var turn = await _t.RunningJobAsync();

        var applied = await Profiles().ApplyAsync(turn, default);

        Assert.AreEqual((turn.Session, turn.Resume, turn.Prompt), (applied.Session, applied.Resume, applied.Prompt));
    }

    [TestMethod]
    public async Task A_profiles_first_turn_starts_its_own_session_with_the_handoff_and_later_turns_resume_it()
    {
        var turn = await _t.RunningJobAsync();
        _plans.Plan = "1. Fix the login\n2. Run the tests";
        var profiles = Profiles();
        await profiles.ApplyAsync(turn, default);   // the plan turn, on the default profile

        var first = await profiles.ApplyAsync(turn with { Profile = "deepseek", Resume = true, Prompt = "Implement it." }, default);
        var second = await profiles.ApplyAsync(turn with { Profile = "deepseek", Resume = true, Prompt = "Next." }, default);

        Assert.AreNotEqual(turn.Session, first.Session, "its own session, never the Claude one");
        Assert.IsFalse(first.Resume);
        Assert.Contains("You're taking over work item #", first.Prompt);
        Assert.Contains("1. Fix the login", first.Prompt);
        Assert.Contains("follow it step by step", first.Prompt);
        Assert.EndsWith("Implement it.", first.Prompt.TrimEnd());
        Assert.AreEqual((first.Session, true, "Next."), (second.Session, second.Resume, second.Prompt), "resumed, no second handoff");
    }

    [TestMethod]
    public async Task Coming_back_to_a_session_that_missed_turns_gets_a_catch_up_note()
    {
        var turn = await _t.RunningJobAsync();
        var profiles = Profiles();
        await profiles.ApplyAsync(turn, default);
        await profiles.ApplyAsync(turn with { Profile = "deepseek" }, default);

        var back = await profiles.ApplyAsync(turn with { Resume = true, Prompt = "Fix the review comments." }, default);

        Assert.AreEqual((turn.Session, true), (back.Session, back.Resume));
        Assert.StartsWith(ProfileSessions.CatchUp, back.Prompt);
    }

    [TestMethod]
    public async Task If_the_first_turn_ran_on_a_profile_the_jobs_own_session_starts_fresh_later()
    {
        var turn = await _t.RunningJobAsync();
        var profiles = Profiles();
        await profiles.ApplyAsync(turn with { Profile = "deepseek" }, default);   // plan on DeepSeek: the Claude session never started

        var claude = await profiles.ApplyAsync(turn with { Resume = true, Prompt = "Implement it." }, default);

        Assert.AreEqual(turn.Session, claude.Session);
        Assert.IsFalse(claude.Resume, "nothing to resume");
        Assert.Contains("You're taking over", claude.Prompt);
    }

    [TestMethod]
    public async Task A_job_from_before_profiles_resumes_as_before()
    {
        var turn = await _t.RunningJobAsync();

        var resumed = await Profiles().ApplyAsync(turn with { Resume = true }, default);   // no rows at all: an old job

        Assert.IsTrue(resumed.Resume);
        Assert.AreEqual(turn.Prompt, resumed.Prompt);
    }

    private ProfileSessions Profiles() => new(_sessions, _plans, _t.Jobs);

    private sealed class MemorySessions : IJobSessions
    {
        private readonly Dictionary<(long, string), Guid> _rows = [];

        public Task<(Guid Session, bool Created)> GetOrCreateAsync(JobId job, string profile, Guid proposed, CancellationToken cancellationToken)
        {
            var created = _rows.TryAdd((job.Value, profile), proposed);
            return Task.FromResult((_rows[(job.Value, profile)], created));
        }

        public Task<IReadOnlyList<string>> ProfilesAsync(JobId job, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. _rows.Keys.Where(k => k.Item1 == job.Value).Select(k => k.Item2)]);
    }

    private sealed class MemoryPlans : IJobPlans
    {
        public string? Plan { get; set; }

        public Task SaveAsync(JobId job, string plan, DateTimeOffset submittedAt, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> GetAsync(JobId job, CancellationToken cancellationToken) => Task.FromResult(Plan);
    }
}
