using Agentd.Application.Setup;

namespace Agentd.Host.Configuration;

/// <summary>
/// Setup is complete once <c>Agentd:Setup:CompletedAt</c> is set: in <c>agentd.json</c> (read from disk each time,
/// since the wizard writes it while the daemon runs and the file isn't reloaded) or in the configuration
/// (e.g. <c>AGENTD_Setup__CompletedAt</c> in a container). Once complete, it stays complete.
/// </summary>
internal sealed class SetupState(ConfigHome home, IConfiguration? configuration = null) : ISetupState
{
    public const string Key = "Setup:CompletedAt";

    private volatile bool _complete;

    public bool IsComplete => _complete || (_complete = IsCompleteIn(home, configuration));

    public static bool IsCompleteIn(ConfigHome home, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (!string.IsNullOrWhiteSpace(configuration?[$"{Options.AgentdOptions.Section}:{Key}"]))
        {
            return true;
        }

        if (!File.Exists(home.ConfigFile))
        {
            return false;
        }

        try
        {
            var file = new ConfigurationBuilder().AddJsonFile(home.ConfigFile, optional: true, reloadOnChange: false).Build();
            return !string.IsNullOrWhiteSpace(file[Key]);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException)
        {
            return false;   // an unreadable agentd.json fails the daemon's own startup; it never counts as "set up"
        }
    }
}
