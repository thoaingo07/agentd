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
