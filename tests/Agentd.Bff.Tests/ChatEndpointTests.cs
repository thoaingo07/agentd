using System.Net;
using System.Text.Json;
using Agentd.Application.Chats;
using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Bff.Tests;

/// <summary>Code questions from the Web UI (the agent's turns are covered in ChatServiceTests).</summary>
[TestClass]
public sealed class ChatEndpointTests
{
    private readonly Store _store = new();

    [TestMethod]
    public async Task Chats_list_and_read_with_their_conversation()
    {
        var id = _store.Add(ChatStatus.Open);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var list = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/chats", UriKind.Relative))).RootElement;
        var detail = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/chats/{id}", UriKind.Relative))).RootElement;
        using var missing = await client.GetAsync(new Uri("/api/chats/99", UriKind.Relative));

        CollectionAssert.AreEqual(new[] { "id", "author", "status", "repos", "model", "effort", "inChat", "title", "messages", "createdAt", "updatedAt" },
            list[0].EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        CollectionAssert.AreEqual(new[] { "id", "author", "status", "repos", "model", "effort", "inChat", "messages", "thinking" }, detail.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(("what does the sync do?", false), (detail.GetProperty("messages")[0].GetProperty("text").GetString(), detail.GetProperty("thinking").GetBoolean()));
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task Questions_need_text_and_valid_settings_and_messages_reach_only_open_chats()
    {
        var closed = _store.Add(ChatStatus.Closed);
        var open = _store.Add(ChatStatus.Open);
        await using var app = await StartAsync();

        using var empty = await PostAsync(app, "/api/chats", new { text = " " });
        using var model = await PostAsync(app, "/api/chats", new { text = "why?", model = "gpt 4!" });
        using var late = await PostAsync(app, $"/api/chats/{closed}/messages", new { text = "one more" });
        using var missing = await PostAsync(app, "/api/chats/99/messages", new { text = "hello" });
        using var blank = await PostAsync(app, $"/api/chats/{open}/messages", new { text = " " });
        using var close = await PostAsync(app, $"/api/chats/{open}/close", null);

        Assert.AreEqual((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (empty.StatusCode, model.StatusCode));
        Assert.AreEqual((HttpStatusCode.Conflict, HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.Accepted), (late.StatusCode, missing.StatusCode, blank.StatusCode, close.StatusCode));
        Assert.AreEqual(ChatStatus.Closed, _store.Rows[open].Status);
        StringAssert.Contains(_store.Notices.Single(), "Chat closed by local", "kept for the page: there's no thread");
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<IChatStore>(_store);
        builder.Services.AddSingleton(new ChatService(_store, UnusedProxy.Of<IRepositoryRegistry>(), UnusedProxy.Of<IWorktreeManager>(), UnusedProxy.Of<IBrainstormAgent>(),
            UnusedProxy.Of<IMessagingProviderRegistry>(), NullLogger<ChatService>.Instance));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplication app, string url, object? body) =>
        await (await new AntiforgeryClient(app).InitAsync()).PostAsync(url, body);

    private sealed class Store : IChatStore
    {
        public Dictionary<long, Chat> Rows { get; } = [];

        public List<string> Notices { get; } = [];

        public long Add(string status)
        {
            var id = Rows.Count + 1L;
            Rows[id] = new Chat(id, "local", ProviderKey.From("web"), null, null, status, ["sysmin"], null, null, null, []);
            return id;
        }

        public Task<long> InsertAsync(string author, ProviderKey provider, string? threadId, string? spaceId, IReadOnlyList<string> repositories, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Chat?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(Rows.GetValueOrDefault(id));

        public Task<Chat?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveAsync(Chat chat, CancellationToken cancellationToken)
        {
            Rows[chat.Id] = chat;
            return Task.CompletedTask;
        }

        public Task AddMessageAsync(long chatId, string direction, string author, string text, CancellationToken cancellationToken)
        {
            Notices.Add(text);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(long chatId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>([new("in", "local", "what does the sync do?", DateTimeOffset.UnixEpoch)]);

        public Task<IReadOnlyList<ChatSummary>> ListAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChatSummary>>([.. Rows.Values.Select(c => new ChatSummary(c.Id, c.Author, c.Status, c.Repositories, null, null, false, "what does the sync do?", 1,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))]);

        public Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
