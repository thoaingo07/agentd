using System.Net;
using System.Text.Json;
using Agentd.Application.Messaging;
using Agentd.Application.Monitor;
using Agentd.Application.Ports;
using Agentd.Domain.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

/// <summary>The watch list from the Web UI (the monitor itself is covered in the Application tests).</summary>
[TestClass]
public sealed class WatchEndpointTests
{
    private readonly Store _store = new();

    [TestMethod]
    public async Task Watches_are_listed_with_a_waiting_fix_and_their_notices()
    {
        var id = _store.Add(new PendingFix("/wt", "c0ffee1234", "Fixed the null check.", [], DateTimeOffset.UnixEpoch));
        _store.Messages.Add((id, "👀 Watching PR !3944"));
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var list = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/watches", UriKind.Relative))).RootElement;
        var detail = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/watches/{id}", UriKind.Relative))).RootElement;
        using var missing = await client.GetAsync(new Uri("/api/watches/99", UriKind.Relative));

        CollectionAssert.AreEqual(new[] { "id", "repo", "pullRequestId", "title", "watchedBy", "active", "fixRounds", "pending", "inChat", "createdAt" },
            list[0].EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        Assert.AreEqual(("c0ffee1234", false), (list[0].GetProperty("pending").GetProperty("commit").GetString(), list[0].GetProperty("inChat").GetBoolean()));
        Assert.AreEqual("👀 Watching PR !3944", detail.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task Answers_are_the_threads_and_need_a_waiting_fix_except_unwatch()
    {
        var id = _store.Add(null);
        await using var app = await StartAsync();

        using var push = await PostAsync(app, $"/api/watches/{id}/push");
        using var odd = await PostAsync(app, $"/api/watches/{id}/merge");
        using var missing = await PostAsync(app, "/api/watches/99/unwatch");
        using var stop = await PostAsync(app, $"/api/watches/{id}/unwatch");
        using var noPr = await PostAsync(app, "/api/watches", new { repo = "sysmin" });

        Assert.AreEqual((HttpStatusCode.Conflict, HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.OK, HttpStatusCode.BadRequest),
            (push.StatusCode, odd.StatusCode, missing.StatusCode, stop.StatusCode, noPr.StatusCode));
        Assert.AreEqual("🛑 Stopped watching PR !3944.", JsonDocument.Parse(await stop.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString());
        Assert.IsFalse(_store.Rows.Single().Active);
        Assert.AreEqual("🛑 Stopped watching PR !3944.", _store.Messages.Single().Text, "kept for the page: there's no thread");
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<IPrWatchStore>(_store);
        builder.Services.AddSingleton(new PrWatchService(_store, UnusedProxy.Of<IRepositoryRegistry>(), UnusedProxy.Of<IPullRequestService>(), UnusedProxy.Of<IMessagingProviderRegistry>()));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplication app, string url, object? body = null) =>
        await (await new AntiforgeryClient(app).InitAsync()).PostAsync(url, body);

    private sealed class Store : IPrWatchStore
    {
        public List<PrWatch> Rows { get; } = [];

        public List<(long Watch, string Text)> Messages { get; } = [];

        public long Add(PendingFix? pending)
        {
            var id = Rows.Count + 1L;
            Rows.Add(new PrWatch(id, "sysmin", 3944, "Deploy", "local", ProviderKey.From("web"), null, null, true, 0, [], null, null, pending, DateTimeOffset.UnixEpoch));
            return id;
        }

        public Task<(long Id, bool Created)> InsertAsync(string repository, int pullRequestId, string title, string watchedBy, ProviderKey provider, string? threadId, string? spaceId,
            IReadOnlyList<int> seenComments, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PrWatch?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(Rows.FirstOrDefault(w => w.Id == id));

        public Task<PrWatch?> FindActiveAsync(string repository, int pullRequestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PrWatch?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PrWatch>> ListActiveAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PrWatch>>([.. Rows.Where(w => w.Active)]);

        public Task<bool> StopAsync(long id, CancellationToken cancellationToken)
        {
            var i = Rows.FindIndex(w => w.Id == id && w.Active);
            if (i >= 0)
            {
                Rows[i] = Rows[i] with { Active = false, Pending = null };
            }

            return Task.FromResult(i >= 0);
        }

        public Task AddMessageAsync(long id, string text, CancellationToken cancellationToken)
        {
            Messages.Add((id, text));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PrWatchMessage>> ListMessagesAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PrWatchMessage>>([.. Messages.Where(m => m.Watch == id).Select(m => new PrWatchMessage(m.Text, DateTimeOffset.UnixEpoch))]);

        public Task UpdateAsync(long id, int fixRounds, IReadOnlyList<int> seenComments, int? lastBuildId, DateTimeOffset? signalAt, PendingFix? pending, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
