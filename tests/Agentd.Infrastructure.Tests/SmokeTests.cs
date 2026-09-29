namespace Agentd.Infrastructure.Tests;

// Repository integration tests arrive in Phase 1; they use Agentd.Migrator to create the schema.
[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public void Assembly_under_test_is_referenced() =>
        Assert.AreEqual("Agentd.Infrastructure.Persistence", typeof(Agentd.Infrastructure.Persistence.AssemblyMarker).Assembly.GetName().Name);
}
