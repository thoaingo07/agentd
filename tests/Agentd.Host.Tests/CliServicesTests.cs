using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentd.Host.Tests;

[TestClass]
public sealed class CliServicesTests
{
    [TestMethod]
    public void The_cli_composition_is_valid_with_development_validation()
    {
        var home = Directory.CreateTempSubdirectory("agentd-cli-home-").FullName;
        Environment.SetEnvironmentVariable(ConfigHome.Variable, home);
        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [],
                EnvironmentName = Environments.Development,
                ContentRootPath = home,
            });
            builder.Configuration["ConnectionStrings:agentd"] = "Host=127.0.0.1;Port=1;Database=unused";
            builder.AddAgentdCore([]);

            // Development turns these on in the real hosts: every registration must be constructible.
            using var services = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            Assert.IsNotNull(services.GetRequiredService<Application.Ports.IMcpTokenIssuer>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigHome.Variable, null);
            Directory.Delete(home, recursive: true);
        }
    }
}

[TestClass]
public sealed class DaemonContentRootTests
{
    [TestMethod]
    [DoNotParallelize]
    public void Started_from_a_directory_without_settings_it_uses_its_own_directory()
    {
        var original = Directory.GetCurrentDirectory();
        var elsewhere = Directory.CreateTempSubdirectory("agentd-cwd-").FullName;
        try
        {
            Directory.SetCurrentDirectory(elsewhere);
            Assert.AreEqual(AppContext.BaseDirectory, DaemonHost.ContentRoot());

            File.WriteAllText(Path.Combine(elsewhere, "appsettings.json"), "{}");
            Assert.IsNull(DaemonHost.ContentRoot(), "a directory with settings stays the content root");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(elsewhere, recursive: true);
        }
    }
}
