namespace Agentd.Domain.Tests;

[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public void Assembly_under_test_is_referenced() =>
        Assert.AreEqual("Agentd.Domain", typeof(Agentd.Domain.AssemblyMarker).Assembly.GetName().Name);
}
