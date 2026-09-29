namespace Agentd.ArchitectureTests;

[TestClass]
public sealed class DependencyRuleTests
{
    public static IEnumerable<object[]> Rules => DependencyRules.All.Select(r => new object[] { r });

    [TestMethod]
    [DynamicData(nameof(Rules))]
    public void Rule_holds_for_every_source_project(DependencyRule rule)
    {
        var violations = ProjectGraph.Source.Values.SelectMany(rule.Violations).ToList();

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void Infrastructure_projects_do_not_reference_each_other()
    {
        var violations = ProjectGraph.Source.Values.SelectMany(DependencyRules.CrossInfrastructureViolations).ToList();

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void Expected_projects_are_discovered()
    {
        foreach (var name in new[] { "Agentd.Domain", "Agentd.Application", "Agentd.Infrastructure.Persistence", "Agentd.Bff", "Agentd.Mcp", "Agentd.Host" })
        {
            Assert.IsTrue(ProjectGraph.Source.ContainsKey(name), $"{name} not found under src/");
        }
    }

    [TestMethod]
    public void Parser_detects_a_forbidden_reference()
    {
        var application = ProjectInfo.Parse("Agentd.Application", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Agentd.Infrastructure.Persistence\Agentd.Infrastructure.Persistence.csproj" />
                <PackageReference Include="Npgsql" />
              </ItemGroup>
            </Project>
            """);
        var rule = DependencyRules.All.Single(r => r.Description == "Application is framework-free");

        var violations = rule.Violations(application).ToList();

        Assert.HasCount(2, violations);
        StringAssert.Contains(violations[0], "Agentd.Infrastructure.Persistence");
        StringAssert.Contains(violations[1], "Npgsql");
    }

    [TestMethod]
    public void Orm_packages_are_rejected_everywhere()
    {
        var host = ProjectInfo.Parse("Agentd.Host", """<Project><ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore" /></ItemGroup></Project>""");
        var rule = DependencyRules.All.Single(r => r.Description.StartsWith("No ORM", StringComparison.Ordinal));

        Assert.HasCount(1, rule.Violations(host).ToList());
    }
}
