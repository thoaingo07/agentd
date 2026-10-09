using Agentd.Application.Jobs;
using Agentd.Infrastructure.Claude;

namespace Agentd.Infrastructure.Tests.Claude;

[TestClass]
public sealed class ProfileEnvironmentTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("agentd-profile-").FullName;

    [TestMethod]
    public void A_profile_turn_talks_to_the_provider_and_never_to_the_subscription()
    {
        var options = new ClaudeOptions { TranscriptRoot = Path.Combine(_home, "logs"), OAuthToken = "sk-ant-oat01-subscription" };
        var env = SafeEnvironment.Build(new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["ANTHROPIC_API_KEY"] = "leak" }, options);
        var profile = new ModelProfile { BaseUrl = "https://api.deepseek.com/anthropic", Model = "deepseek-flash", SmallModel = "deepseek-flash", ApiKey = "sk-deepseek" };
        profile.Environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"] = "786432";

        ProfileEnvironment.Apply(env, "deepseek", profile, options);

        Assert.AreEqual("https://api.deepseek.com/anthropic", env["ANTHROPIC_BASE_URL"]);
        Assert.AreEqual("sk-deepseek", env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.AreEqual(("deepseek-flash", "deepseek-flash", "deepseek-flash", "deepseek-flash"),
            (env["ANTHROPIC_MODEL"], env["ANTHROPIC_DEFAULT_SONNET_MODEL"], env["ANTHROPIC_DEFAULT_HAIKU_MODEL"], env["CLAUDE_CODE_SUBAGENT_MODEL"]));
        Assert.IsFalse(env.ContainsKey(options.OAuthTokenVariable), "no fallback to the Claude subscription");
        Assert.IsFalse(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.AreEqual("786432", env["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
        Assert.AreEqual(Path.Combine(_home, "claude", "profiles", "deepseek"), env["CLAUDE_CONFIG_DIR"], "its own config and sessions");
        Assert.IsTrue(Directory.Exists(env["CLAUDE_CONFIG_DIR"]));
        Assert.AreEqual("/usr/bin", env["PATH"]);
    }

    [TestMethod]
    public void A_profile_turn_doesnt_pass_the_claude_model()
    {
        var options = new ClaudeOptions { Model = "claude-opus-5-5" };
        var request = new Application.Ports.AgentRunRequest(new Domain.Jobs.ValueObjects.JobId(1), Domain.Jobs.ValueObjects.WorkItemId.From(1),
            new Domain.Jobs.ValueObjects.WorktreePath("/wt"), Domain.Jobs.ValueObjects.ClaudeSessionId.New(), "go", Resume: false);

        var onProfile = ClaudeArgs.Build(request with { Profile = "deepseek" }, options, null).ToList();
        var withModel = ClaudeArgs.Build(request with { Profile = "deepseek", Model = "deepseek-v4-pro" }, options, null).ToList();

        Assert.DoesNotContain("--model", onProfile, "the profile's ANTHROPIC_MODEL decides");
        Assert.AreEqual("deepseek-v4-pro", withModel[withModel.IndexOf("--model") + 1], "a step's own model still wins");
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
