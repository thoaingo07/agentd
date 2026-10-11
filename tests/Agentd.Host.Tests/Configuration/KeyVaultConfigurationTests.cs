using Agentd.Host.Configuration;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace Agentd.Host.Tests.Configuration;

[TestClass]
public sealed class KeyVaultConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentd-keyvault-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigHome _home;

    public KeyVaultConfigurationTests() => _home = new ConfigHome(_root).EnsureCreated();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    [DataRow("Artifacts--S3--SecretAccessKey", null, "Agentd:Artifacts:S3:SecretAccessKey")]
    [DataRow("ConnectionStrings--agentd", null, "ConnectionStrings:agentd")]
    [DataRow("agentd-AzureDevOps--Pat", "agentd-", "Agentd:AzureDevOps:Pat")]
    [DataRow("other-app-Password", "agentd-", null)]
    [DataRow("agentd-", "agentd-", null)]
    public void Secret_names_map_with_double_dashes_for_colons_and_the_prefix_picks_agentds(string name, string? prefix, string? key) =>
        Assert.AreEqual(key, KeyVaultConfigurationSource.KeyFor(name, prefix));

    [TestMethod]
    public void The_vault_beats_secrets_json_and_an_AGENTD_variable_still_beats_the_vault()
    {
        new SecretStore(_home).Set("AzureDevOps:Pat", "from-secrets", "tngo");
        new SecretStore(_home).Set("KeyVaultTest:Overridden", "from-secrets", "tngo");
        File.WriteAllText(_home.ConfigFile, """{ "KeyVault": { "Uri": "https://my-vault.vault.azure.net/" } }""");
        Environment.SetEnvironmentVariable("AGENTD_KeyVaultTest__Overridden", "from-env");   // its own variable: test classes run in parallel
        var vault = new Vault(("AzureDevOps--Pat", "from-vault", true), ("KeyVaultTest--Overridden", "from-vault", true), ("Old--Secret", "disabled", false),
            ("ConnectionStrings--agentd", "Host=vault-db", true));
        string? seenUri = null;

        try
        {
            var config = new ConfigurationManager();
            config.AddConfigHome(_home, [], c =>
            {
                seenUri = c[KeyVaultConfigurationSource.UriKey];
                return new KeyVaultConfigurationSource(vault, null, "my-vault.vault.azure.net");
            });

            Assert.AreEqual("https://my-vault.vault.azure.net/", seenUri, "the vault's URL comes from agentd.json (or AGENTD_KeyVault__Uri)");
            Assert.AreEqual("from-vault", config["Agentd:AzureDevOps:Pat"], "the vault beats secrets.json");
            Assert.AreEqual("from-env", config["Agentd:KeyVaultTest:Overridden"], "an AGENTD_ variable beats the vault");
            Assert.AreEqual("Host=vault-db", config.GetConnectionString("agentd"));
            Assert.IsNull(config["Agentd:Old:Secret"], "disabled secrets are skipped");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTD_KeyVaultTest__Overridden", null);   // here, not in Dispose: methods run in parallel
        }
    }

    [TestMethod]
    public void A_vault_that_cant_be_read_is_a_warning_naming_only_the_vault()
    {
        var warnings = new StringWriter();
        var vault = new Vault(("AzureDevOps--Pat", "pat-SECRET", true)) { Failure = new RequestFailedException(403, "Caller is not authorized to perform action on resource.\nMore detail") };

        var config = new ConfigurationBuilder().Add(new KeyVaultConfigurationSource(vault, null, "my-vault.vault.azure.net", warnings)).Build();

        Assert.IsNull(config["Agentd:AzureDevOps:Pat"]);
        StringAssert.Contains(warnings.ToString(), "Azure Key Vault my-vault.vault.azure.net couldn't be read (RequestFailedException: Caller is not authorized");
        Assert.DoesNotContain("pat-SECRET", warnings.ToString());
        Assert.DoesNotContain("More detail", warnings.ToString(), "one line");
    }

    [TestMethod]
    public void Only_an_https_vault_url_is_accepted_and_none_means_no_vault()
    {
        var none = new ConfigurationBuilder().Build();
        var http = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [KeyVaultConfigurationSource.UriKey] = "http://my-vault.vault.azure.net/" }).Build();

        Assert.IsNull(KeyVaultConfigurationSource.From(none));
        Assert.ThrowsExactly<InvalidOperationException>(() => KeyVaultConfigurationSource.From(http));
    }

    /// <summary>A vault in memory, through the SDK's mocking constructor.</summary>
    private sealed class Vault(params (string Name, string Value, bool Enabled)[] secrets) : SecretClient
    {
        public Exception? Failure { get; init; }

        public override Pageable<SecretProperties> GetPropertiesOfSecrets(CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            var properties = secrets.Select(s => new SecretProperties(s.Name) { Enabled = s.Enabled }).ToList();
            return Pageable<SecretProperties>.FromPages([Page<SecretProperties>.FromValues(properties, null, new FakeResponse())]);
        }

        public override Response<KeyVaultSecret> GetSecret(string name, string? version = null, SecretContentType? outContentType = null, CancellationToken cancellationToken = default) =>
            Response.FromValue(new KeyVaultSecret(name, secrets.Single(s => s.Name == name).Value), new FakeResponse());
    }

    private sealed class FakeResponse : Response
    {
        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

        protected override bool TryGetHeader(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
