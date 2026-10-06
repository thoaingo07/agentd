using Agentd.Host.Configuration;
using Agentd.Host.Workers;
using Microsoft.Extensions.Configuration;

namespace Agentd.Host.Tests;

[TestClass]
public sealed class ConfigHomeTests
{
    [TestMethod]
    public void Honors_AGENTD_HOME_and_creates_private_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentd-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            var home = ConfigHome.Resolve(name => name == ConfigHome.Variable ? root : null).EnsureCreated();

            Assert.AreEqual(root, home.Root);
            foreach (var folder in new[] { "config", "repos", "worktrees", "logs", "claude", "run" })
            {
                Assert.IsTrue(Directory.Exists(Path.Combine(root, folder)), folder);
                if (!OperatingSystem.IsWindows())
                {
                    Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(root, folder)), folder);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Defaults_to_dot_agentd_in_the_user_profile()
    {
        var home = ConfigHome.Resolve(_ => null);

        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentd"), home.Root);
    }

    [TestMethod]
    public void Agentd_json_and_AGENTD_variables_layer_over_the_home_defaults()
    {
        var root = Directory.CreateTempSubdirectory("agentd-home-").FullName;
        const string Variable = "AGENTD_Jobs__Tag";
        try
        {
            var home = new ConfigHome(root).EnsureCreated();
            File.WriteAllText(home.ConfigFile, """
                { "AzureDevOps": { "Organization": "from-json", "Project": "p" }, "Jobs": { "Tag": "json-tag" } }
                """);
            Environment.SetEnvironmentVariable(Variable, "env-tag");
            var config = new ConfigurationManager();
            config.AddInMemoryCollection([new("Agentd:AzureDevOps:Organization", "from-appsettings")]);

            config.AddConfigHome(home, ["--Agentd:Jobs:ClaimTag=from-args"]);

            Assert.AreEqual("from-json", config["Agentd:AzureDevOps:Organization"], "agentd.json beats appsettings");
            Assert.AreEqual("env-tag", config["Agentd:Jobs:Tag"], "AGENTD_ variables beat agentd.json");
            Assert.AreEqual("from-args", config["Agentd:Jobs:ClaimTag"], "command line wins");
            Assert.AreEqual(home.Worktrees, config["Agentd:Git:WorktreeRoot"], "worktrees default inside the home");
            CollectionAssert.IsSubsetOf(new[] { "AzureDevOps", "Jobs", "Git" }, config.GetSection("Agentd").GetChildren().Select(c => c.Key).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:7780", "http://127.0.0.1:7780/mcp")]
    [DataRow("http://0.0.0.0:7780", "http://127.0.0.1:7780/mcp")]
    [DataRow("http://[::]:5000", "http://127.0.0.1:5000/mcp")]
    [DataRow("http://+:5000", "http://127.0.0.1:5000/mcp")]
    [DataRow("http://localhost:5123", "http://localhost:5123/mcp")]
    public void Mcp_url_points_at_the_hosts_own_endpoint(string address, string expected) =>
        Assert.AreEqual(expected, McpUrlFromServer.For(address));

    [TestMethod]
    public void The_embedded_appsettings_give_the_defaults_without_a_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentd-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationManager();   // no appsettings.json, like the single-file agentd

            config.AddConfigHome(ConfigHome.Resolve(name => name == ConfigHome.Variable ? root : null).EnsureCreated(), []);

            Assert.AreEqual("http://127.0.0.1:7780", config["Agentd:Web:Urls"], "loopback by default");
            Assert.AreEqual("Warning", config["Logging:LogLevel:Microsoft.AspNetCore"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

