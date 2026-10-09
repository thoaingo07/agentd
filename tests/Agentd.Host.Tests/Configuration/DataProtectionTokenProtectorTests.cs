using Agentd.Host.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Agentd.Host.Tests.Configuration;

[TestClass]
public sealed class DataProtectionTokenProtectorTests
{
    [TestMethod]
    public void A_token_is_encrypted_at_rest_and_comes_back_with_the_same_keys_only()
    {
        var keys = new EphemeralDataProtectionProvider();
        var protector = new DataProtectionTokenProtector(keys);

        var stored = protector.Protect("refresh-SECRET-token");

        Assert.DoesNotContain("SECRET", System.Text.Encoding.UTF8.GetString(stored));
        Assert.AreEqual("refresh-SECRET-token", protector.Unprotect(stored));
        Assert.IsNull(new DataProtectionTokenProtector(new EphemeralDataProtectionProvider()).Unprotect(stored), "other keys: reconnect, not a crash");
    }
}
