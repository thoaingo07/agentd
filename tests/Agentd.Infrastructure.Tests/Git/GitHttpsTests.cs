using Agentd.Application.Ports;
using Agentd.Infrastructure.Git;

namespace Agentd.Infrastructure.Tests.Git;

[TestClass]
public sealed class GitHttpsTests
{
    private const string Https = "https://dev.azure.com/ermsystem/Portal/_git/sysmin";

    [TestMethod]
    [DataRow("git@ssh.dev.azure.com:v3/ermsystem/Portal/sysmin", Https)]
    [DataRow("git@erm-azdo:v3/ermsystem/Portal/sysmin", Https)]
    [DataRow("ssh://git@ssh.dev.azure.com/v3/ermsystem/Portal/sysmin", Https)]
    [DataRow("ssh://git@ssh.dev.azure.com:22/v3/ermsystem/Portal/sysmin/", Https)]
    [DataRow("ermsystem@vs-ssh.visualstudio.com:v3/ermsystem/Portal/sysmin", Https)]
    [DataRow("git@ssh.dev.azure.com:v3/erm/My%20Project/My%20Repo", "https://dev.azure.com/erm/My%20Project/_git/My%20Repo")]
    [DataRow("git@github.com:thoaingo07/agentd.git", null)]
    [DataRow(Https, null)]
    [DataRow("/tmp/remote.git", null)]
    public void Azure_devops_ssh_remotes_have_an_https_address(string url, string? expected) =>
        Assert.AreEqual(expected, GitCli.AzureDevOpsHttps(url));

    [TestMethod]
    public void Ssh_is_kept_when_https_is_turned_off()
    {
        var on = new GitCli(Microsoft.Extensions.Options.Options.Create(new GitOptions()));
        var off = new GitCli(Microsoft.Extensions.Options.Options.Create(new GitOptions { AzureDevOpsOverHttps = false }));

        Assert.AreEqual(Https, on.Remote("git@erm-azdo:v3/ermsystem/Portal/sysmin"));
        Assert.AreEqual("git@erm-azdo:v3/ermsystem/Portal/sysmin", off.Remote("git@erm-azdo:v3/ermsystem/Portal/sysmin"));
        Assert.AreEqual("git@github.com:o/r.git", on.Remote("git@github.com:o/r.git"));
    }

    [TestMethod]
    public async Task A_clone_made_over_ssh_moves_to_https_and_its_fetches_carry_the_header()
    {
        using var box = new GitSandbox();
        await box.Manager.EnsureCloneAsync(box.Repository, default);   // the clone, made from the local remote
        var clone = box.Manager.ClonePathFor(box.Repository);
        using var server = new HeaderRecorder();
        GitSandbox.Run(clone, "config", $"url.{server.Prefix}.insteadOf", "https://dev.azure.com/");   // Azure DevOps is this test's server
        var credentials = new Credentials(server.Prefix);
        var manager = new GitWorktreeManager(new GitCli(box.Options, credentials), box.Options);

        await Assert.ThrowsAsync<GitException>(() => manager.EnsureCloneAsync(box.Repository with { RemoteUrl = "git@erm-azdo:v3/ermsystem/Portal/sysmin" }, default));

        Assert.AreEqual(Https, GitSandbox.Run(clone, "config", "--get", "remote.origin.url"));
        Assert.AreEqual("Bearer t0ken", server.Authorization, "the fetch sent the header");
        Assert.DoesNotContain("t0ken", File.ReadAllText(Path.Combine(clone, "config")), "never written to the clone's config");
        Assert.AreEqual(1, credentials.Calls, "asked for the fetch only, not for the local commands");
    }

    [TestMethod]
    public async Task Other_remotes_and_local_commands_get_no_credentials_and_a_failed_sign_in_says_so()
    {
        using var box = new GitSandbox();
        var credentials = new Credentials();
        var git = new GitCli(box.Options, credentials);
        await new GitWorktreeManager(git, box.Options).EnsureCloneAsync(box.Repository, default);   // a local remote
        var clone = box.Manager.ClonePathFor(box.Repository);
        Assert.AreEqual(0, credentials.Calls);

        GitSandbox.Run(clone, "remote", "set-url", "origin", Https);
        var off = new GitCli(Microsoft.Extensions.Options.Options.Create(new GitOptions { AzureDevOpsOverHttps = false, SshKeyPath = box.Options.Value.SshKeyPath }), credentials);
        await off.RunAsync(clone, ["config", "--get", "remote.origin.url"], default);
        credentials.Fail = true;
        var failed = await Assert.ThrowsAsync<GitException>(() => git.RunAsync(clone, ["fetch", "origin"], default));

        Assert.AreEqual(1, credentials.Calls, "only the fetch from Azure DevOps over HTTPS asked");
        StringAssert.Contains(failed.Message, "signing in to Azure DevOps failed: az isn't logged in");
    }

    /// <summary>A local HTTP server that records the Authorization header of git's first request and refuses it.</summary>
    private sealed class HeaderRecorder : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();

        public HeaderRecorder()
        {
            Prefix = $"http://127.0.0.1:{FreePort()}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        Authorization ??= context.Request.Headers["Authorization"];
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                    }
                    catch (System.Net.HttpListenerException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            });
        }

        public string Prefix { get; }

        public string? Authorization { get; private set; }

        public void Dispose() => _listener.Close();

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    private sealed class Credentials(string prefix = "https://dev.azure.com/") : IGitHttpCredentials
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public ValueTask<IReadOnlyList<GitHttpHeader>> GetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Fail
                ? throw new InvalidOperationException("az isn't logged in")
                : ValueTask.FromResult<IReadOnlyList<GitHttpHeader>>([new GitHttpHeader(prefix, "Authorization: Bearer t0ken")]);
        }
    }
}
