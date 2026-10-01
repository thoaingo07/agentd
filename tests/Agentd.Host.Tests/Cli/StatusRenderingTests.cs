using Agentd.Application.Jobs;
using Agentd.Domain.Jobs;
using Agentd.Host.Cli.Commands;

namespace Agentd.Host.Tests.Cli;

[TestClass]
public sealed class StatusRenderingTests
{
    [TestMethod]
    public void Renders_an_aligned_job_table()
    {
        JobStatusRow[] rows =
        [
            new(12, 1234, "sysmin", JobState.Running, TimeSpan.FromMinutes(12), 1, null, null),
            new(13, 98765, "portal-web", JobState.Done, TimeSpan.FromHours(2.5), 2, "https://dev.azure.com/o/p/_git/r/pullrequest/7", null),
            new(14, 42, "sysmin", JobState.Failed, TimeSpan.FromSeconds(30), 1, null, "The agent exited (code 1)\nwithout calling finish."),
        ];

        var text = StatusCommand.Render(rows, includeRecent: true);

        Assert.AreEqual(
            """
            ID  WORK ITEM  REPO        STATE    ELAPSED  ATTEMPT  PR / LAST ERROR
            12  #1234      sysmin      Running  12m      1
            13  #98765     portal-web  Done     2h30m    2        https://dev.azure.com/o/p/_git/r/pullrequest/7
            14  #42        sysmin      Failed   30s      1        The agent exited (code 1) without calling finish.

            """.ReplaceLineEndings("\n"),
            text);
    }

    [TestMethod]
    public void Empty_lists_say_so()
    {
        Assert.AreEqual("No active jobs.\n", StatusCommand.Render([], includeRecent: false));
        Assert.AreEqual("No jobs in the last 24 hours.\n", StatusCommand.Render([], includeRecent: true));
    }

    [TestMethod]
    [DataRow(59, "59s")]
    [DataRow(60 * 5, "5m")]
    [DataRow(60 * 61, "1h01m")]
    [DataRow(60 * 60 * 26, "1d02h")]
    public void Elapsed_is_compact(int seconds, string expected) =>
        Assert.AreEqual(expected, StatusCommand.Elapsed(TimeSpan.FromSeconds(seconds)));
}
