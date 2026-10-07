using System.CommandLine;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agentd.Host.Cli;
using Agentd.Host.Cli.Commands;
using Agentd.Host.Cli.Update;

namespace Agentd.Host.Tests.Cli;

[TestClass]
public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentd-update-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [TestMethod]
    public void Versions_sort_like_semver_with_pre_releases_before_their_release()
    {
        var ordered = new[] { "0.1.9", "v0.2.0-beta.1", "0.2.0-beta.2", "0.2.0-beta.10", "0.2.0", "0.10.0+3f1c2ab" }.Select(v => SemVersion.Parse(v)!).ToList();

        CollectionAssert.AreEqual(ordered, ordered.OrderBy(v => v).ToList());
        Assert.AreEqual("0.10.0", ordered[^1].ToString(), "build metadata is ignored");
        Assert.IsNull(SemVersion.Parse("latest"));
    }

    [TestMethod]
    public void The_channel_picks_the_newest_newer_release_and_to_picks_exactly()
    {
        Release R(string v, bool pre = false) => new("v" + v, SemVersion.Parse(v)!, pre || v.Contains('-', StringComparison.Ordinal), new Dictionary<string, Uri>());
        var releases = new[] { R("0.3.0-beta.1"), R("0.2.1"), R("0.2.0"), R("0.1.0") };
        var current = SemVersion.Parse("0.2.0")!;

        Assert.AreEqual("0.2.1", GitHubReleases.Pick(releases, "stable", current, null)!.Version.ToString());
        Assert.AreEqual("0.3.0-beta.1", GitHubReleases.Pick(releases, "beta", current, null)!.Version.ToString());
        Assert.IsNull(GitHubReleases.Pick(releases, "stable", SemVersion.Parse("0.2.1")!, null), "up to date");
        Assert.AreEqual("0.1.0", GitHubReleases.Pick(releases, "stable", current, SemVersion.Parse("0.1.0"))!.Version.ToString(), "--to can go back");
    }

    [TestMethod]
    public async Task The_new_binary_replaces_the_old_only_when_its_checksum_matches()
    {
        var exe = await ExecutableAsync("old build");
        var (release, http) = Published("new build");

        Assert.IsNull(await new Updater(http, exe, "linux-x64").ApplyAsync(release, default));

        Assert.AreEqual("new build", await File.ReadAllTextAsync(exe));
        Assert.AreEqual("old build", await File.ReadAllTextAsync(Updater.Previous(exe)), "the old binary is kept");
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsTrue(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
        }

        var (tampered, badHttp) = Published("new build", checksumOf: "something else");
        StringAssert.Contains(await new Updater(badHttp, exe, "linux-x64").ApplyAsync(tampered, default), "doesn't match sha256sums.txt");
        Assert.AreEqual("new build", await File.ReadAllTextAsync(exe), "a bad download changes nothing");
        Assert.IsFalse(File.Exists(exe + ".download"));
        StringAssert.Contains(await new Updater(http, exe, "osx-arm64").ApplyAsync(release, default), "no agentd-osx-arm64");
    }

    [TestMethod]
    public async Task Update_installs_migrates_with_the_new_binary_and_restarts_the_service()
    {
        var exe = await ExecutableAsync("old build");
        var (_, http) = Published("new build");
        var units = Directory.CreateDirectory(Path.Combine(_dir, "xdg", "systemd", "user")).FullName;
        await File.WriteAllTextAsync(Path.Combine(units, SystemdUnit.Name), "[Unit]");
        var calls = new List<string>();
        var output = new StringWriter();
        var context = new CliContext(output, new StringWriter(), () => throw new InvalidOperationException(),
            run: (file, args, _, _) => { calls.Add($"{file} {string.Join(' ', args)}"); return Task.FromResult(new ProcessResult(0, string.Empty)); },
            environment: name => name == "XDG_CONFIG_HOME" ? Path.Combine(_dir, "xdg") : null);
        var root = new RootCommand { UpdateCommand.Update(context, () => http, exe) };

        Assert.AreEqual(ExitCodes.Ok, await root.Parse(["update", "--check"]).InvokeAsync());
        StringAssert.Contains(output.ToString(), "agentd 0.2.0 is available");
        Assert.AreEqual("old build", await File.ReadAllTextAsync(exe), "--check changes nothing");

        Assert.AreEqual(ExitCodes.Ok, await root.Parse(["update"]).InvokeAsync());

        Assert.AreEqual("new build", await File.ReadAllTextAsync(exe));
        CollectionAssert.AreEqual(new[] { $"{exe} db migrate", "systemctl --user restart agentd.service" }, calls, "the new binary migrates, then the service restarts");
    }

    private async Task<string> ExecutableAsync(string content)
    {
        var exe = Path.Combine(_dir, "agentd");
        await File.WriteAllTextAsync(exe, content);
        return exe;
    }

    /// <summary>A v0.2.0 release with agentd-linux-x64 and sha256sums.txt, served by a fake GitHub.</summary>
    private static (Release Release, HttpClient Http) Published(string binary, string? checksumOf = null)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(checksumOf ?? binary)));
        var bin = new Uri("https://github.test/v0.2.0/agentd-linux-x64");
        var sums = new Uri("https://github.test/v0.2.0/sha256sums.txt");
        var routes = new Dictionary<string, string>
        {
            [bin.ToString()] = binary,
            [sums.ToString()] = $"{sha}  agentd-linux-x64\n0000  install.sh\n",
            ["https://api.github.com/repos/thoaingo07/agentd/releases?per_page=30"] =
                $$"""[{"tag_name":"v0.2.0","draft":false,"prerelease":false,"assets":[{"name":"agentd-linux-x64","browser_download_url":"{{bin}}"},{"name":"sha256sums.txt","browser_download_url":"{{sums}}"}]}]""",
        };
        var release = new Release("v0.2.0", SemVersion.Parse("0.2.0")!, false, new Dictionary<string, Uri> { ["agentd-linux-x64"] = bin, ["sha256sums.txt"] = sums });
        return (release, new HttpClient(new Fake(routes)));
    }

    private sealed class Fake(Dictionary<string, string> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(routes.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
