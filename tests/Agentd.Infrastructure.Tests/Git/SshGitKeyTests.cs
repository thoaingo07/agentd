using System.Diagnostics;
using Agentd.Infrastructure.Git;

namespace Agentd.Infrastructure.Tests.Git;

[TestClass]
public sealed class SshGitKeyTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();
    private readonly string _key;
    private readonly SshGitKey _gitKey;

    public SshGitKeyTests()
    {
        _key = Path.Combine(_sandbox.Root, "home", "ssh", "id_ed25519");
        var options = Microsoft.Extensions.Options.Options.Create(new GitOptions { SshKeyPath = _key, KnownHostsPath = Path.Combine(_sandbox.Root, "home", "ssh", "known_hosts") });
        _gitKey = new SshGitKey(options, new GitCli(options));
    }

    [TestMethod]
    public async Task Generates_an_owner_only_ed25519_key_and_reports_its_public_half()
    {
        Assert.IsNull(_gitKey.Read());

        var key = await _gitKey.GenerateAsync("agentd@test", CancellationToken.None);

        Assert.StartsWith("ssh-ed25519 ", key.PublicKey);
        Assert.EndsWith(" agentd@test", key.PublicKey);
        Assert.DoesNotContain("PRIVATE", key.PublicKey);
        Assert.AreEqual(Keygen("-lf", _key + ".pub").Split(' ')[1], key.Fingerprint, "the same fingerprint as ssh-keygen -l");
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_key));
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(_key)!));
        }

        Assert.AreEqual(key, _gitKey.Read());
    }

    [TestMethod]
    public async Task Never_overwrites_a_key()
    {
        var first = await _gitKey.GenerateAsync("agentd@test", CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _gitKey.GenerateAsync("again", CancellationToken.None));
        Assert.AreEqual(first.PublicKey, _gitKey.Read()!.PublicKey);
    }

    [TestMethod]
    public async Task Test_lists_the_repository_or_says_why_not()
    {
        var reachable = await _gitKey.TestAsync(_sandbox.RemotePath, CancellationToken.None);
        var missing = await _gitKey.TestAsync(Path.Combine(_sandbox.Root, "nope.git"), CancellationToken.None);

        Assert.IsTrue(reachable.Ok, reachable.Message);
        Assert.Contains("1 branch(es)", reachable.Message);
        Assert.IsFalse(missing.Ok);
        Assert.Contains("generate agentd's SSH key", missing.Fix!);
    }

    public void Dispose() => _sandbox.Dispose();

    private static string Keygen(params string[] args)
    {
        var psi = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }
}
