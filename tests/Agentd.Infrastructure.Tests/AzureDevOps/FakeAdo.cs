using System.Net;
using System.Text;

namespace Agentd.Infrastructure.Tests.AzureDevOps;

/// <summary>Records requests and replays canned responses matched by method + path prefix (query ignored).</summary>
internal sealed class FakeAdo : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string PathPrefix, Func<HttpResponseMessage> Respond)> _routes = [];

    public List<(HttpMethod Method, string Url, string? Body, string? ContentType, string? Auth)> Requests { get; } = [];

    public FakeAdo On(HttpMethod method, string pathPrefix, HttpStatusCode status, string? json = null)
    {
        _routes.Add((method, pathPrefix, () => new HttpResponseMessage(status)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        }));
        return this;
    }

    public HttpClient Client() => new(this) { BaseAddress = new Uri("https://dev.azure.com/") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body, request.Content?.Headers.ContentType?.MediaType, request.Headers.Authorization?.ToString()));
        var path = request.RequestUri!.AbsolutePath;
        var route = _routes.LastOrDefault(r => r.Method == request.Method && path.StartsWith(r.PathPrefix, StringComparison.Ordinal));
        return route.Respond?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
