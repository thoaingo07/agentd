namespace Agentd.Host.Cli;

/// <summary>Output streams and the (lazily built) services a CLI verb runs with.</summary>
internal sealed class CliContext(TextWriter output, TextWriter error, Func<IServiceProvider> services) : IAsyncDisposable
{
    private IServiceProvider? _services;

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
