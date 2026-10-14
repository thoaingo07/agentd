using Agentd.Application.Incidents;

namespace Agentd.Application.Tests.Incidents;

[TestClass]
public sealed class IncidentMonitorOptionsValidatorTests
{
    [TestMethod]
    public void Off_needs_nothing_and_on_needs_valid_unique_apps()
    {
        var validator = new IncidentMonitorOptionsValidator();
        Assert.IsTrue(validator.Validate(null, new IncidentMonitorOptions()).Succeeded);

        var on = new IncidentMonitorOptions { Enabled = true };
        StringAssert.Contains(validator.Validate(null, on).FailureMessage, "list at least one");

        on.Apps.Add(new MonitoredApp { Name = "orders", ResourceId = "/subscriptions/s/resourceGroups/rg/providers/microsoft.insights/components/ai", Repository = "orders" });
        Assert.IsTrue(validator.Validate(null, on).Succeeded, "the provider namespace is case-insensitive");

        on.Apps.Add(new MonitoredApp { Name = "Orders", ResourceId = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Web/sites/x", Repository = " ", RoleName = "a'b" });
        var failure = validator.Validate(null, on).FailureMessage;
        StringAssert.Contains(failure, "'Orders' is listed twice");
        StringAssert.Contains(failure, "Apps:1:ResourceId");
        StringAssert.Contains(failure, "Apps:1:Repository");
        StringAssert.Contains(failure, "Apps:1:RoleName");
    }
}
