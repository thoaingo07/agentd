using System.Net;
using System.Text.Json;
using Agentd.Application.Prompts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class PromptEndpointTests
{
    private readonly Log _log = new();

    [TestMethod]
    public async Task Admins_page_through_prompts_with_filters_and_others_are_refused()
    {
        await using var app = await StartAsync();

        using var page = await GetAsync(app, "/api/prompts?source=job&sourceId=42&step=implement&model=opus&outcome=running&limit=2", "thoaingo07@gmail.com");
        using var next = await GetAsync(app, "/api/prompts?before=2", "thoaingo07@gmail.com");
        using var bad = await GetAsync(app, "/api/prompts?source=email", "thoaingo07@gmail.com");
        using var other = await GetAsync(app, "/api/prompts", "dev@example.com");

        var body = JsonDocument.Parse(await page.Content.ReadAsStringAsync()).RootElement;
        CollectionAssert.AreEqual(
            new[] { "id", "source", "sourceId", "step", "session", "resume", "model", "effort", "profile", "outcome", "error", "startedAt", "finishedAt", "preview", "promptChars", "replyChars", "hasRules" },
            body.GetProperty("prompts")[0].EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        Assert.AreEqual((2, true), (body.GetProperty("prompts").GetArrayLength(), body.GetProperty("hasMore").GetBoolean()));
        Assert.AreEqual((new PromptFilter("job", 42, "implement", "opus", "running"), (long?)null, 3), _log.Asked[0], "one more than the page, to know there's more");
        Assert.AreEqual((new PromptFilter(), (long?)2, 51), _log.Asked[1]);
        Assert.AreEqual((HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Forbidden), (next.StatusCode, bad.StatusCode, other.StatusCode));
    }

    [TestMethod]
    public async Task A_prompt_comes_whole_with_its_rules_and_reply()
    {
        await using var app = await StartAsync();

        using var found = await GetAsync(app, "/api/prompts/7", "thoaingo07@gmail.com");
        using var missing = await GetAsync(app, "/api/prompts/8", "thoaingo07@gmail.com");
        using var other = await GetAsync(app, "/api/prompts/7", "dev@example.com");

        var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual(("Fix the login", "the job rules", "Done."), (body.GetProperty("prompt").GetString(), body.GetProperty("rules").GetString(), body.GetProperty("reply").GetString()));
        Assert.AreEqual((HttpStatusCode.NotFound, HttpStatusCode.Forbidden), (missing.StatusCode, other.StatusCode));
    }

    [TestMethod]
    public async Task A_prompts_transcript_comes_as_the_job_transcripts_events()
    {
        await using var app = await StartAsync();

        using var found = await GetAsync(app, "/api/prompts/7/transcript", "thoaingo07@gmail.com");
        using var missing = await GetAsync(app, "/api/prompts/8/transcript", "thoaingo07@gmail.com");
        using var other = await GetAsync(app, "/api/prompts/7/transcript", "dev@example.com");

        var events = JsonDocument.Parse(await found.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual(2, events.GetArrayLength());
        Assert.AreEqual(("agent.tool_call", 2, 42L), (events[1].GetProperty("type").GetString(), events[1].GetProperty("seq").GetInt32(), events[1].GetProperty("jobId").GetInt64()));
        Assert.AreEqual("Read", events[1].GetProperty("payload").GetProperty("name").GetString(), "the payload as JSON, not a string");
        Assert.AreEqual((HttpStatusCode.NotFound, HttpStatusCode.Forbidden), (missing.StatusCode, other.StatusCode));
    }

    private static async Task<HttpResponseMessage> GetAsync(WebApplication app, string url, string login)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Add("Tailscale-User-Login", login);
        request.Headers.Add("X-Forwarded-For", "100.111.15.109");
        return await app.GetTestClient().SendAsync(request);
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Agentd:Auth:Mode"] = "Tailscale";
        builder.Configuration["Agentd:Auth:Tailscale:AdminLogins:0"] = "thoaingo07@gmail.com";
        builder.Services.AddBff();
        builder.Services.AddSingleton<IPromptLog>(_log);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;   // Serve connects from this machine
            return next(context);
        });
        app.UseBffForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Log : IPromptLog
    {
        public List<(PromptFilter Filter, long? Before, int Limit)> Asked { get; } = [];

        public Task<IReadOnlyList<PromptSummary>> ListAsync(PromptFilter filter, long? before, int limit, CancellationToken cancellationToken)
        {
            Asked.Add((filter, before, limit));
            return Task.FromResult<IReadOnlyList<PromptSummary>>([.. Enumerable.Range(0, 3).Select(i =>
                new PromptSummary(3 - i, PromptSources.Job, 42, "implement", null, false, "opus", "high", null, null, null, DateTimeOffset.UnixEpoch, null, "Fix the login", 13, 0, true))]);
        }

        public Task<PromptDetail?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(id == 7
            ? new PromptDetail(7, PromptSources.Job, 42, "implement", null, false, "opus", "high", null, PromptOutcomes.Ok, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                "Fix the login", "Done.", "the job rules")
            : null);

        public Task<long> StartAsync(PromptStart prompt, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task FinishAsync(long id, string outcome, string? failure, string? reply, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PromptEvent>> TranscriptAsync(long id, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PromptEvent>>([
                new PromptEvent("agent.text", "{\"text\":\"Reading the login.\"}", DateTimeOffset.UnixEpoch),
                new PromptEvent("agent.tool_call", "{\"id\":\"t1\",\"name\":\"Read\"}", DateTimeOffset.UnixEpoch),
            ]);
    }
}
