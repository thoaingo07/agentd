namespace Agentd.Host.Control;

/// <summary>
/// The daemon's control socket, <c>~/.agentd/run/agentd.sock</c>: a second, tiny Kestrel that listens only there, so
/// <c>/control/*</c> can never be reached over TCP. Access control is the file system: the socket is 0600 inside the
/// 0700 <c>run</c> folder (owner only), so there is no token. A stale socket (a crashed daemon) is replaced at start;
/// a live one (another daemon on this home) is left alone. Not on Windows (named pipes later; the CLI uses the database).
/// </summary>
internal sealed partial class ControlSocket(ConfigHome home, IServiceProvider services, ILogger<ControlSocket> logger) : IHostedService, IAsyncDisposable
{
    public const string FileName = "agentd.sock";

    /// <summary>sun_path is 108 bytes on Linux, 104 on macOS, including the terminator.</summary>
    public const int MaxPathBytes = 100;

    private WebApplication? _server;

    public static string PathIn(ConfigHome home) => Path.Combine(home.Run, FileName);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = PathIn(home);
        if (File.Exists(path))
        {
            if (await DaemonClient.IsListeningAsync(path, cancellationToken).ConfigureAwait(false))
            {
                LogInUse(logger, path);
                return;
            }

            File.Delete(path);   // left behind by a daemon that didn't stop cleanly
        }

        if (System.Text.Encoding.UTF8.GetByteCount(path) > MaxPathBytes)
        {
            LogUnavailable(logger, path, $"the path is longer than {MaxPathBytes} bytes (a Unix socket limit); use a shorter AGENTD_HOME");
            return;
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(path));
        var server = builder.Build();
        ControlEndpoints.Map(server, services);
        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            // The daemon works without it: the CLI falls back to the database.
            await server.DisposeAsync().ConfigureAwait(false);
            LogUnavailable(logger, path, ex.Message);
            return;
        }

        _server = server;
        LogListening(logger, path);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is null)
        {
            return;
        }

        await _server.StopAsync(cancellationToken).ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is { } server)
        {
            _server = null;
            await server.DisposeAsync().ConfigureAwait(false);
            File.Delete(PathIn(home));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Control socket listening on {Path}")]
    private static partial void LogListening(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No control socket at {Path} ({Reason}); the CLI reads the database instead")]
    private static partial void LogUnavailable(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another agentd is already listening on {Path}; this one runs without a control socket")]
    private static partial void LogInUse(ILogger logger, string path);
}
