using System.Net.Sockets;

namespace Agentd.Host.Control;

/// <summary>
/// The CLI's side of the control socket: HTTP over <c>~/.agentd/run/agentd.sock</c>. Every call returns null when the
/// daemon isn't there (no socket, refused, or Windows), so verbs fall back to the database.
/// </summary>
internal sealed class DaemonClient : IDisposable
{
    private readonly HttpClient _http;

    private DaemonClient(string path) =>
        _http = new HttpClient(new SocketsHttpHandler { ConnectCallback = (_, ct) => ConnectAsync(path, ct) })
        {
            BaseAddress = new Uri("http://agentd/"),
            Timeout = TimeSpan.FromSeconds(15),
        };

    public static DaemonClient? TryCreate(ConfigHome home)
    {
        var path = ControlSocket.PathIn(home);
        return !OperatingSystem.IsWindows() && File.Exists(path) ? new DaemonClient(path) : null;
    }

    public static async Task<bool> IsListeningAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var _ = await ConnectAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ControlJob>?> StatusAsync(bool all, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<ControlJob>>(new Uri($"control/status?all={(all ? "true" : "false")}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>The queued job, the daemon's refusal, or null when the daemon isn't reachable.</summary>
    public async Task<(ControlRun? Run, ControlError? Error)?> RunAsync(int workItemId, string? repository, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"control/run/{workItemId}{(repository is null ? string.Empty : $"?repo={Uri.EscapeDataString(repository)}")}";
            using var response = await _http.PostAsync(new Uri(url, UriKind.Relative), null, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<ControlRun>(cancellationToken).ConfigureAwait(false), null)
                : (null, await response.Content.ReadFromJsonAsync<ControlError>(cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static async ValueTask<Stream> ConnectAsync(string path, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
