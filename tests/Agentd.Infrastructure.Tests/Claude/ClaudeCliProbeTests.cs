using Agentd.Infrastructure.Claude;

namespace Agentd.Infrastructure.Tests.Claude;

[TestClass]
public sealed class ClaudeCliProbeTests : IDisposable
{
    private const string GoodToken = "sk-ant-oat01-good-token-0123456789";

    // A stand-in for the Claude Code CLI: records its environment, answers like the real one.
    private const string FakeClaude = """
        #!/bin/sh
        dir="$(dirname "$0")"
        env > "$dir/env.txt"
        case "$1" in
          --version) echo "2.1.300 (Claude Code)" ;;
          auth) echo '{"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"max"}' ;;
          -p)
            if [ "$ANTHROPIC_AUTH_TOKEN" = "sk-good-provider-key" ] || [ "$CLAUDE_CODE_OAUTH_TOKEN" = "sk-ant-oat01-good-token-0123456789" ] || { [ -z "$CLAUDE_CODE_OAUTH_TOKEN" ] && [ -f "$dir/logged-in" ]; }; then
              echo '{"result":"agentd-ok"}'
            else
              echo "Invalid bearer token" >&2; exit 1
            fi ;;
        esac
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("agentd-claude-").FullName;
    private readonly ClaudeCliProbe _probe;

    public ClaudeCliProbeTests()
    {
        var binary = Path.Combine(_dir, "claude");
        File.WriteAllText(binary, FakeClaude.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _probe = new ClaudeCliProbe(Microsoft.Extensions.Options.Options.Create(new ClaudeOptions { Binary = binary }));
    }

    [TestMethod]
    public async Task Status_reports_the_version_and_the_servers_login()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The fake CLI is a shell script.");
        }

        var login = await _probe.StatusAsync(CancellationToken.None);

        Assert.AreEqual(new Application.Setup.ClaudeLogin(true, "2.1.300 (Claude Code)", true, "claude.ai", "max"), login);
    }

    [TestMethod]
    public async Task A_good_token_answers_and_a_bad_one_fails_with_a_fix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The fake CLI is a shell script.");
        }

        var good = await _probe.TestAsync(GoodToken, CancellationToken.None);
        var bad = await _probe.TestAsync("sk-ant-oat01-expired-0000000000", CancellationToken.None);

        Assert.IsTrue(good.Ok, good.Message);
        Assert.Contains("the token", good.Message);
        Assert.IsFalse(bad.Ok);
        Assert.Contains("Invalid bearer token", bad.Message);
        Assert.Contains("claude setup-token", bad.Fix!);
    }

    [TestMethod]
    public async Task Without_a_token_the_servers_login_is_tested()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The fake CLI is a shell script.");
        }

        var loggedOut = await _probe.TestAsync(null, CancellationToken.None);
        File.WriteAllText(Path.Combine(_dir, "logged-in"), string.Empty);
        var loggedIn = await _probe.TestAsync(null, CancellationToken.None);

        Assert.IsFalse(loggedOut.Ok);
        Assert.Contains("claude auth login", loggedOut.Fix!);
        Assert.IsTrue(loggedIn.Ok, loggedIn.Message);
    }

    [TestMethod]
    public async Task The_cli_gets_the_agents_environment_without_daemon_secrets()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The fake CLI is a shell script.");
        }

        const string Variable = "AGENTD_ClaudeProbeTest__Secret";
        Environment.SetEnvironmentVariable(Variable, "daemon-only");
        try
        {
            await _probe.TestAsync(GoodToken, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }

        var env = File.ReadAllText(Path.Combine(_dir, "env.txt"));
        Assert.DoesNotContain("daemon-only", env);
        Assert.Contains($"CLAUDE_CODE_OAUTH_TOKEN={GoodToken}", env);
    }

    [TestMethod]
    public async Task A_provider_is_tested_with_its_own_endpoint_and_key_and_never_the_subscription()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The fake CLI is a shell script.");
        }

        var profile = new Application.Jobs.ModelProfile { BaseUrl = "https://api.deepseek.com/anthropic", Model = "deepseek-flash[1m]", SmallModel = "deepseek-flash", ApiKey = "sk-good-provider-key" };
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", GoodToken);
        Application.Setup.StepCheck good, bad;
        try
        {
            good = await _probe.TestProfileAsync("deepseek", profile, CancellationToken.None);
            var env = File.ReadAllText(Path.Combine(_dir, "env.txt"));
            Assert.Contains("ANTHROPIC_BASE_URL=https://api.deepseek.com/anthropic", env);
            Assert.Contains("ANTHROPIC_MODEL=deepseek-flash[1m]", env);
            Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", env);
            profile.ApiKey = "sk-wrong";
            bad = await _probe.TestProfileAsync("deepseek", profile, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
        }

        Assert.IsTrue(good.Ok, good.Message);
        Assert.Contains("deepseek-flash[1m] answered", good.Message);
        Assert.IsFalse(bad.Ok);
        Assert.Contains("API key", bad.Fix!);
    }

    [TestMethod]
    public async Task A_missing_cli_says_how_to_install_it()
    {
        var probe = new ClaudeCliProbe(Microsoft.Extensions.Options.Options.Create(new ClaudeOptions { Binary = Path.Combine(_dir, "no-such-claude") }));

        var login = await probe.StatusAsync(CancellationToken.None);
        var check = await probe.TestAsync(GoodToken, CancellationToken.None);

        Assert.IsFalse(login.Installed);
        Assert.IsFalse(check.Ok);
        Assert.Contains("npm install -g @anthropic-ai/claude-code", check.Fix!);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
