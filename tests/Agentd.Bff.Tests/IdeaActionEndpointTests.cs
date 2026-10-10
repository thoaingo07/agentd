using System.Net;
using System.Reflection;
using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Bff.Tests;

/// <summary>Starting and talking to an idea from the Web UI (the agent's turns are covered in IdeaServiceTests).</summary>
[TestClass]
public sealed class IdeaActionEndpointTests
{
    private readonly Store _store = new();

    [TestMethod]
    public async Task Starting_an_idea_needs_text_and_valid_settings()
    {
        await using var app = await StartAsync();

        using var empty = await PostAsync(app, "/api/ideas", new { text = " " });
        using var model = await PostAsync(app, "/api/ideas", new { text = "dark mode", model = "gpt 4!" });

        Assert.AreEqual((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (empty.StatusCode, model.StatusCode));
        Assert.IsEmpty(_store.Rows);
    }

    [TestMethod]
    public async Task A_message_reaches_an_open_idea_and_a_finished_or_missing_one_says_so()
    {
        await using var app = await StartAsync();
        var open = _store.Add(IdeaStatus.Proposed);
        var finished = _store.Add(IdeaStatus.Discarded);

        using var change = await PostAsync(app, $"/api/ideas/{open}/messages", new { text = IdeaService.LabelChange });
        using var late = await PostAsync(app, $"/api/ideas/{finished}/messages", new { text = "one more thing" });
        using var missing = await PostAsync(app, "/api/ideas/99/messages", new { text = "hello" });
        using var empty = await PostAsync(app, $"/api/ideas/{open}/messages", new { text = " " });

        Assert.AreEqual((HttpStatusCode.Accepted, HttpStatusCode.Conflict, HttpStatusCode.NotFound, HttpStatusCode.BadRequest),
            (change.StatusCode, late.StatusCode, missing.StatusCode, empty.StatusCode));
        CollectionAssert.AreEqual(new[] { "in:local:✏️ Change", "out:agentd:✏️ Tell me what to change, and I'll send a revised list." }, _store.Messages);
    }

    [TestMethod]
    public async Task Settings_change_from_the_next_reply()
    {
        await using var app = await StartAsync();
        var id = _store.Add(IdeaStatus.Brainstorming);

        using var changed = await SendAsync(app, HttpMethod.Put, $"/api/ideas/{id}/settings", new { model = "sonnet", effort = "LOW" });
        using var invalid = await SendAsync(app, HttpMethod.Put, $"/api/ideas/{id}/settings", new { effort = "huge" });
        using var missing = await SendAsync(app, HttpMethod.Put, "/api/ideas/99/settings", new { model = "opus" });

        Assert.AreEqual((HttpStatusCode.NoContent, HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (changed.StatusCode, invalid.StatusCode, missing.StatusCode));
        Assert.AreEqual(("sonnet", "low"), (_store.Rows[id].Model, _store.Rows[id].Effort));
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<IIdeaStore>(_store);
        builder.Services.AddSingleton(new IdeaService(_store, Unused<IRepositoryRegistry>(), Unused<IWorktreeManager>(), Unused<IBrainstormAgent>(),
            Unused<IMessagingProviderRegistry>(), NullLogger<IdeaService>.Instance));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplication app, string url, object body) =>
        await (await new AntiforgeryClient(app).InitAsync()).PostAsync(url, body);

    private static async Task<HttpResponseMessage> SendAsync(WebApplication app, HttpMethod method, string url, object body) =>
        await (await new AntiforgeryClient(app).InitAsync()).SendAsync(method, url, body);

    /// <summary>A dependency these requests never reach: any call fails the test.</summary>
    private static T Unused<T>() where T : class => DispatchProxy.Create<T, UnusedProxy>();

#pragma warning disable CA1852 // DispatchProxy needs a non-sealed type
    internal class UnusedProxy : DispatchProxy
#pragma warning restore CA1852
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException($"{targetMethod?.Name} isn't used here.");
    }

    private sealed class Store : IIdeaStore
    {
        public Dictionary<long, Idea> Rows { get; } = [];

        public List<string> Messages { get; } = [];

        public long Add(string status)
        {
            var id = Rows.Count + 1;
            Rows[id] = new Idea(id, "sysmin", "Dark mode", "local", IdeaService.Web, null, null, status, null, null, null, null, null, []);
            return id;
        }

        public Task<long> InsertAsync(string repository, string title, string author, ProviderKey provider, string? threadId, string? spaceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Idea?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(Rows.GetValueOrDefault(id));

        public Task<Idea?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveAsync(Idea idea, CancellationToken cancellationToken)
        {
            Rows[idea.Id] = idea;
            return Task.CompletedTask;
        }

        public Task AddMessageAsync(long ideaId, string direction, string author, string text, CancellationToken cancellationToken)
        {
            Messages.Add($"{direction}:{author}:{text}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long ideaId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<IdeaSummary>> ListSummariesAsync(long? id, int? workItem, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
