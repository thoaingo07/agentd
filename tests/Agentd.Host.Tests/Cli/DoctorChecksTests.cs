using Agentd.Host.Cli.Commands;

namespace Agentd.Host.Tests.Cli;

[TestClass]
public sealed class DoctorChecksTests
{
    [TestMethod]
    [DataRow("git version 2.47.3", "2.47.3")]
    [DataRow("v24.21.0", "24.21.0")]
    [DataRow("2.1.292 (Claude Code)", "2.1.292")]
    [DataRow("v3.19.0+g3d8990f", "3.19.0")]
    public void Versions_are_read_from_tool_output(string output, string expected) =>
        Assert.AreEqual(Version.Parse(expected), DoctorChecks.ParseVersion(output));

    [TestMethod]
    public void A_repositorys_build_files_name_the_tools_it_needs_from_the_main_file()
    {
        var needs = DoctorChecks.Toolchains([
            "src/web/libs/deep/package.json", "package.json", "pnpm-lock.yaml", "Sysmin-Api.slnx", "src/Api/Api.csproj",
            "charts/api/Chart.yaml", "taskfile.yml", "src/Api/Dockerfile", "README.md",
        ]);

        CollectionAssert.AreEqual(new[] { "dotnet", "node", "pnpm", "helm", "docker", "task" }, needs.Select(n => n.Tool).ToArray());
        Assert.AreEqual("package.json", needs.Single(n => n.Tool == "node").Because, "the shallowest one, not a sub-project's");
        Assert.AreEqual("Sysmin-Api.slnx", needs.Single(n => n.Tool == "dotnet").Because);
        Assert.IsEmpty(DoctorChecks.Toolchains(["README.md", "docs/index.md"]));
        CollectionAssert.AreEqual(new[] { "mvn", "java" }, DoctorChecks.Toolchains(["pom.xml"]).Select(n => n.Tool).ToArray());
    }

    [TestMethod]
    public void Results_show_as_ok_warning_or_failure_and_only_failures_and_warnings_show_a_fix()
    {
        Assert.AreEqual("✅ git: 2.47\n", DoctorCommand.Format(new("git", true, "2.47", "unused")));
        Assert.AreEqual("⚠️ Node.js: v22 (agentd wants 24)\n   fix: install Node.js 24\n", DoctorCommand.Format(new("Node.js", true, "v22 (agentd wants 24)", "install Node.js 24", Warning: true)));
        Assert.AreEqual("❌ PostgreSQL: down\n   fix: start it\n", DoctorCommand.Format(new("PostgreSQL", false, "down", "start it")));
    }
}
