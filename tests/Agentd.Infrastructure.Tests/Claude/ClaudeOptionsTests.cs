using Agentd.Infrastructure.Claude;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.Claude;

[TestClass]
public sealed class ClaudeOptionsTests
{
    [TestMethod]
    public void Git_fetch_is_allowed_in_both_modes_and_push_in_neither()
    {
        var options = new ClaudeOptions();

        CollectionAssert.Contains(options.AllowedTools.ToList(), "Bash(git fetch:*)");
        CollectionAssert.Contains(options.ReadOnlyTools.ToList(), "Bash(git fetch:*)");
        Assert.IsFalse(options.AllowedTools.Concat(options.ReadOnlyTools).Any(t => t.Contains("git push", StringComparison.Ordinal)), "pushing stays with agentd");
    }

    [TestMethod]
    public void With_mcp_the_cli_sends_permission_prompts_to_agentd()
    {
        var request = new Agentd.Application.Ports.AgentRunRequest(new Agentd.Domain.Jobs.ValueObjects.JobId(1), Agentd.Domain.Jobs.ValueObjects.WorkItemId.From(5615),
            new Agentd.Domain.Jobs.ValueObjects.WorktreePath("/wt"), Agentd.Domain.Jobs.ValueObjects.ClaudeSessionId.New(), "go", false);

        var withMcp = ClaudeArgs.Build(request, new ClaudeOptions(), "/tmp/mcp.json").ToList();
        var without = ClaudeArgs.Build(request, new ClaudeOptions(), null).ToList();

        Assert.AreEqual("mcp__agentd__permission", withMcp[withMcp.IndexOf("--permission-prompt-tool") + 1]);
        CollectionAssert.Contains(withMcp, "--strict-mcp-config", "only agentd's MCP server, no claude.ai connectors");
        CollectionAssert.Contains(without, "--strict-mcp-config", "and no MCP at all without agentd's");
        CollectionAssert.DoesNotContain(without, "--permission-prompt-tool", "no MCP server: nothing could answer");
    }

    [TestMethod]
    public void Brainstorm_turns_are_read_only_isolated_and_use_the_ideas_model_and_effort()
    {
        var args = ClaudeBrainstormAgent.Args(new Agentd.Application.Ideas.BrainstormTurn(1, "/wt", Guid.NewGuid(), false, "hi", "opus", "high"), new ClaudeOptions()).ToList();

        Assert.AreEqual("opus", args[args.IndexOf("--model") + 1]);
        Assert.AreEqual("high", args[args.IndexOf("--effort") + 1]);
        CollectionAssert.Contains(args, "--strict-mcp-config");
        Assert.AreEqual("Edit,Write,MultiEdit,NotebookEdit", args[args.IndexOf("--disallowedTools") + 1]);
        var tools = args[args.IndexOf("--allowedTools") + 1];
        Assert.DoesNotContain("mcp__", tools, "no agentd tools: ideas aren't jobs");
        Assert.DoesNotContain("git commit", tools);
        CollectionAssert.DoesNotContain(args, "--mcp-config");
    }

    [TestMethod]
    public void Configured_tools_are_added_to_the_defaults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentd:Claude:AllowedTools:0"] = "Bash(git rev-parse:*)",
            ["Agentd:Claude:ReadOnlyTools:0"] = "Bash(git branch:*)",
        }).Build();
        using var services = new ServiceCollection().AddOptions<ClaudeOptions>().Bind(config.GetSection(ClaudeOptions.Section)).Services.BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<ClaudeOptions>>().Value;

        CollectionAssert.Contains(options.AllowedTools.ToList(), "Bash(git rev-parse:*)");
        CollectionAssert.Contains(options.AllowedTools.ToList(), "Bash(git fetch:*)", "defaults are kept");
        CollectionAssert.Contains(options.ReadOnlyTools.ToList(), "Bash(git branch:*)");
        CollectionAssert.Contains(options.ReadOnlyTools.ToList(), "Read");
    }
}
