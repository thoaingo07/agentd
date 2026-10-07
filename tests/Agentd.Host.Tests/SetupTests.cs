using Agentd.Bff.Setup;
using Agentd.Host.Cli;
using Agentd.Host.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Host.Tests;

[TestClass]
public sealed class SetupTests : IDisposable
{
    private readonly ConfigHome _home = new ConfigHome(Directory.CreateTempSubdirectory("agentd-setup-").FullName).EnsureCreated();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    [TestMethod]
    public void Only_the_tokens_hash_is_stored_owner_only()
    {
        var tokens = new SetupToken(_home.SetupTokenFile);

        var token = tokens.Issue();

        Assert.IsGreaterThanOrEqualTo(43, token.Length, "32 random bytes, base64url");
        Assert.DoesNotContain(token, File.ReadAllText(_home.SetupTokenFile));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_home.SetupTokenFile));
        }

        Assert.IsTrue(tokens.Verify(token));
        Assert.IsTrue(new SetupToken(_home.SetupTokenFile).Verify(token), "the daemon sees a token the CLI issued");
        Assert.IsFalse(tokens.Verify(token[..^1] + (token[^1] == 'A' ? 'B' : 'A')));
        Assert.IsFalse(tokens.Verify(null));
    }

    [TestMethod]
    public void A_revoked_token_is_dead()
    {
        var tokens = new SetupToken(_home.SetupTokenFile);
        var token = tokens.Issue();

        tokens.Revoke();

        Assert.IsFalse(tokens.Verify(token));
    }

    [TestMethod]
    public void Setup_is_complete_once_agentd_json_or_the_configuration_says_so()
    {
        Assert.IsFalse(SetupState.IsCompleteIn(_home));

        File.WriteAllText(_home.ConfigFile, """{ "Setup": { "CompletedAt": "2026-10-06T12:00:00Z" } }""");
        Assert.IsTrue(SetupState.IsCompleteIn(_home), "written by the wizard while the daemon runs: read from disk");

        File.Delete(_home.ConfigFile);
        var env = new ConfigurationBuilder().AddInMemoryCollection([new("Agentd:Setup:CompletedAt", "2026-10-06")]).Build();
        Assert.IsTrue(SetupState.IsCompleteIn(_home, env), "AGENTD_Setup__CompletedAt (containers)");
    }

    [TestMethod]
    [DataRow("http://0.0.0.0:7780", "http://127.0.0.1:7780")]
    [DataRow("http://*:7780;https://+:7781", "http://127.0.0.1:7780;https://127.0.0.1:7781")]
    [DataRow("http://agentd.example.com", "http://127.0.0.1")]
    [DataRow("http://[::]:7780", "http://127.0.0.1:7780")]
    [DataRow("http://127.0.0.1:7780", "http://127.0.0.1:7780")]
    [DataRow("http://localhost:7780", "http://localhost:7780")]
    public void Loopback_keeps_the_scheme_and_port(string urls, string expected) =>
        Assert.AreEqual(expected, HostUrls.Loopback(urls));

    [TestMethod]
    [DataRow(false, "http://127.0.0.1:7780")]
    [DataRow(true, "http://0.0.0.0:7780")]
    public void Until_setup_is_complete_the_daemon_listens_on_loopback_only(bool complete, string expected)
    {
        if (complete)
        {
            File.WriteAllText(_home.ConfigFile, """{ "Setup": { "CompletedAt": "2026-10-06" } }""");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration["Agentd:Web:Urls"] = "http://0.0.0.0:7780";

        HostUrls.ApplyDefault(builder, _home);

        Assert.AreEqual(expected, builder.Configuration["urls"]);
    }

    [TestMethod]
    [DataRow(new string[0], "http://127.0.0.1:7780/setup?token=abc")]
    [DataRow(new[] { "http://[::]:7790" }, "http://127.0.0.1:7790/setup?token=abc")]
    [DataRow(new[] { "http://127.0.0.1:7796/" }, "http://127.0.0.1:7796/setup?token=abc")]
    public void The_link_points_at_the_listening_address(string[] addresses, string expected) =>
        Assert.AreEqual(expected, SetupLink.For(addresses, "abc"));

    [TestMethod]
    [DataRow("http://127.0.0.1:7796/setup?token=abc", "ssh -L 7796:127.0.0.1:7796 <this server>")]
    [DataRow("http://127.0.0.1/setup?token=abc", "ssh -L 80:127.0.0.1:80 <this server>")]
    public void The_tunnel_hint_uses_the_links_own_port(string link, string expected)
    {
        Assert.AreEqual(expected, SetupLink.Tunnel(link));
        Assert.Contains(expected, SetupLink.Hint(link));
    }

    [TestMethod]
    public async Task Setup_link_prints_a_new_working_link()
    {
        var old = new SetupToken(_home.SetupTokenFile).Issue();

        Assert.AreEqual(ExitCodes.Ok, await RunAsync("setup-link"));

        var link = new Uri(_out.ToString().Split('\n')[0].Trim());
        Assert.AreEqual("/setup", link.AbsolutePath);
        var token = Uri.UnescapeDataString(link.Query["?token=".Length..]);
        Assert.IsTrue(new SetupToken(_home.SetupTokenFile).Verify(token));
        Assert.IsFalse(new SetupToken(_home.SetupTokenFile).Verify(old), "the previous link stops working");
    }

    [TestMethod]
    public async Task Setup_link_refuses_once_set_up()
    {
        File.WriteAllText(_home.ConfigFile, """{ "Setup": { "CompletedAt": "2026-10-06" } }""");

        Assert.AreEqual(ExitCodes.Conflict, await RunAsync("setup-link"));

        Assert.Contains("already set up", _err.ToString());
        Assert.IsFalse(File.Exists(_home.SetupTokenFile));
    }

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        Directory.Delete(_home.Root, recursive: true);
    }

    private Task<int> RunAsync(params string[] args)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return AgentdCli.InvokeAsync(args, _out, _err, () => services, (_, _) => Task.FromResult(0), CancellationToken.None, home: () => _home);
    }
}
