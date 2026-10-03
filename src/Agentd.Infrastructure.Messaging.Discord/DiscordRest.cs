using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Agentd.Application.Messaging;

namespace Agentd.Infrastructure.Messaging.Discord;

/// <summary>
/// The few Discord REST calls agentd needs. <paramref name="client"/> comes from IHttpClientFactory per
/// call (the bot token is added by <see cref="DiscordAuthHandler"/>), so a singleton can hold this safely.
/// Failures become <see cref="MessagingDeliveryException"/>: 429 carries <c>retry_after</c>, 5xx and
/// network errors are transient, other 4xx (missing access, unknown channel) are permanent.
/// </summary>
public sealed class DiscordRest(Func<HttpClient> client)
{
    public Task<JsonNode?> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct) => SendAsync(HttpMethod.Post, path, Json(body), ct);

    public Task<JsonNode?> DeleteAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, path, null, ct);

    public Task<JsonNode?> PatchAsync(string path, JsonNode body, CancellationToken ct) => SendAsync(HttpMethod.Patch, path, Json(body), ct);

    /// <summary>A message with files: <c>payload_json</c> plus <c>files[n]</c>.</summary>
    public Task<JsonNode?> PostWithFilesAsync(string path, JsonObject payload, IReadOnlyList<Attachment> files, CancellationToken ct)
    {
        var form = new MultipartFormDataContent();
        payload["attachments"] = new JsonArray(files.Select((f, i) => (JsonNode)new JsonObject { ["id"] = i, ["filename"] = f.FileName }).ToArray());
        form.Add(Json(payload), "payload_json");
        for (var i = 0; i < files.Count; i++)
        {
            var file = new ByteArrayContent(files[i].Content.ToArray());
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(files[i].ContentType);
            form.Add(file, string.Create(CultureInfo.InvariantCulture, $"files[{i}]"), files[i].FileName);
        }

        return SendAsync(HttpMethod.Post, path, form, ct);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await client().SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new MessagingDeliveryException($"Discord unreachable: {ex.Message}", permanent: false, inner: ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }

            var body = TryParse(text);
            var message = $"Discord returned {(int)response.StatusCode}: {body?["message"]?.GetValue<string>() ?? response.ReasonPhrase}";
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var seconds = body?["retry_after"]?.GetValue<double>() ?? response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1;
                throw new MessagingDeliveryException(message, permanent: false, retryAfter: TimeSpan.FromSeconds(Math.Max(seconds, 0.1)));
            }

            throw new MessagingDeliveryException(message, permanent: (int)response.StatusCode is >= 400 and < 500);
        }
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Adds <c>Authorization: Bot &lt;token&gt;</c> and the User-Agent Discord requires.</summary>
public sealed class DiscordAuthHandler(Microsoft.Extensions.Options.IOptions<DiscordOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.Value.BotToken);
        request.Headers.UserAgent.ParseAdd("DiscordBot (https://github.com/thoaingo07/agentd, 1.0)");
        return base.SendAsync(request, cancellationToken);
    }
}
