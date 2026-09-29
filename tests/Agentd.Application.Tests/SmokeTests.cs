namespace Agentd.Application.Tests;

[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public void Assembly_under_test_is_referenced() =>
        Assert.AreEqual("Agentd.Application", typeof(Agentd.Application.AssemblyMarker).Assembly.GetName().Name);
}
