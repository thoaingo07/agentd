using Agentd.Host.Cli;
using Agentd.Host.Configuration;
using Microsoft.Extensions.Configuration;

namespace Agentd.Host.Tests;

[TestClass]
public sealed class SecretStoreTests : IDisposable
{
    private const string Pat = "pat-SECRET-0123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentd-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigHome _home;

    public SecretStoreTests() => _home = new ConfigHome(_root).EnsureCreated();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void A_secret_is_encrypted_at_rest_private_and_listed_without_its_value()
    {
        var store = new SecretStore(_home);

        store.Set("AzureDevOps:Pat", Pat, "tngo");

        var text = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain(Pat, text, "never in plain text");
        Assert.Contains("AzureDevOps:Pat", text);
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
        }

        var listed = store.List().Single();
        Assert.AreEqual(("AzureDevOps:Pat", "tngo"), (listed.Key, listed.UpdatedBy));
        Assert.AreEqual(Pat, new SecretStore(_home).Load().Values["AzureDevOps:Pat"], "another instance (the daemon) decrypts it with the same key ring");
        Assert.IsTrue(store.Remove("AzureDevOps:Pat"));
        Assert.IsFalse(store.Remove("AzureDevOps:Pat"));
        Assert.IsEmpty(store.List());
    }

    [TestMethod]
    public void Secrets_are_configuration_above_agentd_json_and_below_AGENTD_variables()
    {
        File.WriteAllText(_home.ConfigFile, """{ "AzureDevOps": { "Pat": "from-json", "Auth": "Pat" }, "Discord": { "BotToken": "from-json" } }""");
        var store = new SecretStore(_home);
        store.Set("AzureDevOps:Pat", Pat, "tngo");
        store.Set("Discord:BotToken", "from-secrets", "tngo");
        store.Set("ConnectionStrings:agentd", "Host=db;Password=pw", "tngo");
        Environment.SetEnvironmentVariable("AGENTD_Discord__BotToken", "from-env");
        try
        {
            var config = new ConfigurationManager();
            config.AddConfigHome(_home, []);

            Assert.AreEqual(Pat, config["Agentd:AzureDevOps:Pat"], "a secret beats agentd.json");
            Assert.AreEqual("Pat", config["Agentd:AzureDevOps:Auth"], "other agentd.json values stay");
            Assert.AreEqual("from-env", config["Agentd:Discord:BotToken"], "AGENTD_ variables beat secrets (containers)");
            Assert.AreEqual("Host=db;Password=pw", config.GetConnectionString("agentd"), "ConnectionStrings:* keys aren't prefixed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTD_Discord__BotToken", null);
        }
    }

    [TestMethod]
    public void A_secret_that_does_not_decrypt_is_skipped_with_a_warning_naming_only_the_key()
    {
        new SecretStore(_home).Set("Discord:BotToken", "bot-SECRET", "tngo");
        Directory.Delete(_home.Keys, recursive: true);   // the key ring is lost
        Directory.CreateDirectory(_home.Keys);
        var warnings = new StringWriter();

        var config = new ConfigurationBuilder().Add(new SecretsConfigurationSource(_home, warnings)).Build();

        Assert.IsNull(config["Agentd:Discord:BotToken"]);
        Assert.Contains("'Discord:BotToken' can't be decrypted", warnings.ToString());
        Assert.DoesNotContain("bot-SECRET", warnings.ToString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("has space")]
    [DataRow("a::b")]
    [DataRow(":leading")]
    [DataRow("semi;colon")]
    public void Invalid_names_are_refused(string key)
    {
        Assert.IsFalse(SecretStore.IsValidKey(key));
        Assert.ThrowsExactly<ArgumentException>(() => new SecretStore(_home).Set(key, "v", "tngo"));
    }

    [TestMethod]
    public async Task The_cli_reads_the_value_from_stdin_never_from_arguments_and_lists_names_only()
    {
        var (code, output, error) = await Cli(["secrets", "set", "AzureDevOps:Pat"], Pat);
        Assert.AreEqual(ExitCodes.Ok, code, error);
        Assert.DoesNotContain(Pat, output);

        (code, _, error) = await Cli(["secrets", "set", "AzureDevOps:Pat", "pat-on-the-command-line"], "unused");
        Assert.AreEqual(ExitCodes.Usage, code);
        Assert.Contains("shell history", error);
        Assert.AreEqual(Pat, new SecretStore(_home).Load().Values["AzureDevOps:Pat"], "the argument wasn't stored");

        (code, output, _) = await Cli(["secrets", "list"], null);
        Assert.AreEqual(ExitCodes.Ok, code);
        Assert.Contains("AzureDevOps:Pat", output);
        Assert.DoesNotContain(Pat, output);

        (code, output, _) = await Cli(["secrets", "remove", "AzureDevOps:Pat"], null);
        Assert.AreEqual((ExitCodes.Ok, true), (code, output.Contains("Removed", StringComparison.Ordinal)));
        Assert.AreEqual(ExitCodes.Error, (await Cli(["secrets", "remove", "AzureDevOps:Pat"], null)).Code);
        Assert.AreEqual(ExitCodes.Usage, (await Cli(["secrets", "set", "Empty:Value"], "")).Code, "an empty value stores nothing");
    }

    private async Task<(int Code, string Out, string Err)> Cli(string[] args, string? secret)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await AgentdCli.InvokeAsync(args, output, error, () => throw new InvalidOperationException("secrets don't need services"),
            (_, _) => Task.FromResult(0), CancellationToken.None, () => _home, _ => secret);
        return (code, output.ToString(), error.ToString());
    }
}
