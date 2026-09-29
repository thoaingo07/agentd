using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>Small JSON helpers over the typed HttpClient. Error bodies are surfaced without auth details.</summary>
internal static class AdoHttp
{
    public const string ApiVersion = "7.1";
    public const string CommentsApiVersion = "7.1-preview.4";

    public static async Task<JsonNode?> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(url, UriKind.Relative), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    public static async Task<JsonNode?> SendAsync(HttpClient http, HttpMethod method, string url, JsonNode body, string mediaType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, mediaType),
        };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    public static async Task<(HttpStatusCode Status, string Body)> SendRawAsync(HttpClient http, HttpMethod method, string url, JsonNode body, string mediaType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, mediaType),
        };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        ThrowIfSignInRedirect(response);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Azure DevOps answers a rejected credential with a redirect to its sign-in page (302, or 203 with
    /// HTML) instead of 401. Surface that as a clear authentication error rather than a JSON parse failure.
    /// </summary>
    internal static void ThrowIfSignInRedirect(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var isHtml = response.Content.Headers.ContentType?.MediaType is "text/html";
        if (status is >= 300 and < 400 || status == 203 || (isHtml && response.IsSuccessStatusCode))
        {
            throw new AdoException(
                "Azure DevOps rejected the credentials (redirected to sign-in). With Auth=AzCli the `az login` identity may not " +
                "belong to the organization's tenant; use a PAT (Auth=Pat, secret Agentd:AzureDevOps:Pat).",
                401);
        }
    }

    public static string Esc(string segment) => Uri.EscapeDataString(segment);

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ThrowIfSignInRedirect(response);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryMessage(text) ?? response.ReasonPhrase ?? "request failed";
            throw new AdoException($"Azure DevOps returned {(int)response.StatusCode}: {message}", (int)response.StatusCode);
        }

        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static string? TryMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }
}
