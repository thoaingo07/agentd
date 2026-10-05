using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Agentd.Bff.Tests;

/// <summary>What the SPA does: fetch the token from /bff/antiforgery once, then send it (and the cookie) on unsafe requests.</summary>
internal sealed class AntiforgeryClient(WebApplication app)
{
    private readonly HttpClient _http = app.GetTestClient();

    public string? Cookie { get; private set; }

    public string? Token { get; private set; }

    public async Task<AntiforgeryClient> InitAsync()
    {
        using var response = await _http.GetAsync(new Uri("/bff/antiforgery", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        Cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        Token = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        return this;
    }

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, string? token = null, string? cookie = null) =>
        SendAsync(HttpMethod.Post, url, body, token, cookie);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? token = null, string? cookie = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        if ((token ?? Token) is { } t)
        {
            request.Headers.Add("X-XSRF-TOKEN", t);
        }

        if ((cookie ?? Cookie) is { } c)
        {
            request.Headers.Add("Cookie", c);
        }

        return await _http.SendAsync(request);
    }
}
