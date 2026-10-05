namespace Agentd.Host.Cli;

/// <summary>Output streams and the (lazily built) services a CLI verb runs with.</summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
/// <param name="services">Builds the services on first use.</param>
/// <param name="home">The config home (default: <c>AGENTD_HOME</c> or <c>~/.agentd</c>).</param>
/// <param name="readSecret">Reads a secret value after a prompt (default: piped stdin, or the terminal without echo).</param>
internal sealed class CliContext(
    TextWriter output,
    TextWriter error,
    Func<IServiceProvider> services,
    Func<ConfigHome>? home = null,
    Func<string, string?>? readSecret = null) : IAsyncDisposable
{
    private IServiceProvider? _services;
    private ConfigHome? _home;

    /// <summary>Resolved on first use and created (0700) if missing.</summary>
    public ConfigHome Home => _home ??= (home ?? (() => ConfigHome.Resolve()))().EnsureCreated();

    public Func<string, string?> ReadSecret { get; } = readSecret ?? ConsoleSecret.Read;

    public TextWriter Out { get; } = output;

    public TextWriter Error { get; } = error;

    /// <summary>Built on first use, so <c>--help</c> and parse errors never touch configuration or the database.</summary>
    public IServiceProvider Services => _services ??= services();

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    public async ValueTask DisposeAsync()
    {
        if (_services is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
