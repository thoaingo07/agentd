using System.Net;
using System.Text.Json;
using Agentd.Application.Inbox;
using Agentd.Application.Messaging;
using Agentd.Bff.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class InboxEndpointTests
{
    private readonly Inbox _inbox = new();

    [TestMethod]
    public async Task The_inbox_lists_threads_for_the_signed_in_person_and_Mine_keeps_theirs()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var all = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/inbox", UriKind.Relative))).RootElement;
        await client.GetStringAsync(new Uri("/api/inbox?view=mine&limit=9999", UriKind.Relative));

        var thread = all.GetProperty("threads")[0];
        Assert.AreEqual(("idea", 4L, true, "Create these?"), (thread.GetProperty("source").GetString(), thread.GetProperty("sourceId").GetInt64(),
            thread.GetProperty("waiting").GetBoolean(), thread.GetProperty("question").GetProperty("markdown").GetString()));
        Assert.AreEqual("✅ Create", thread.GetProperty("question").GetProperty("options")[0].GetProperty("label").GetString());
        Assert.AreEqual((1, 2), (all.GetProperty("counts").GetProperty("waiting").GetInt32(), all.GetProperty("counts").GetProperty("unread").GetInt32()));
        Assert.AreEqual(("local", (string?)null, 100), _inbox.Asked[0], "Everyone: no recipient filter");
        Assert.AreEqual(("local", "local", InboxEndpoints.MaxThreads), _inbox.Asked[1], "Mine: addressed to them; the limit is capped");
    }

    [TestMethod]
    public async Task A_thread_reads_newest_first_and_marking_it_read_is_per_person()
    {
        await using var app = await StartAsync();

        var items = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(new Uri("/api/inbox/idea/4", UriKind.Relative))).RootElement;
        using var read = await PostAsync(app, "/api/inbox/idea/4/read", new { until = 12 });
        using var badSource = await PostAsync(app, "/api/inbox/email/4/read", new { until = 12 });
        using var noUntil = await PostAsync(app, "/api/inbox/idea/4/read", new { });

        Assert.AreEqual("Question", items[0].GetProperty("kind").GetString());
        Assert.AreEqual(HttpStatusCode.NoContent, read.StatusCode);
        Assert.AreEqual(("local", "idea", 4L, 12L), _inbox.Read.Single());
        Assert.AreEqual((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (badSource.StatusCode, noUntil.StatusCode));
    }

    [TestMethod]
    public async Task A_reply_needs_text_and_a_known_source()
    {
        await using var app = await StartAsync();

        using var empty = await PostAsync(app, "/api/inbox/idea/4/reply", new { text = " " });
        using var badSource = await PostAsync(app, "/api/inbox/email/4/reply", new { text = "1" });
        using var noService = await PostAsync(app, "/api/inbox/chat/4/reply", new { text = "thanks" });
        using var incident = await PostAsync(app, "/api/inbox/incident/4/reply", new { text = "fix it" });

        Assert.AreEqual((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (empty.StatusCode, badSource.StatusCode));
        Assert.AreEqual(HttpStatusCode.NotFound, noService.StatusCode, "no chats here");
        Assert.AreEqual(HttpStatusCode.Conflict, incident.StatusCode, "an incident's thread is readable, answers come next");
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplication app, string url, object? body) =>
        await (await new AntiforgeryClient(app).InitAsync()).PostAsync(url, body);

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<IInbox>(_inbox);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Inbox : IInbox
    {
        private static readonly InboxItem s_question = new(12, "idea", 4, "💡 Dark mode", "local", "Question", "Create these?",
            [new MessageOption("idea-create", "✅ Create")], DateTimeOffset.UnixEpoch);

        public List<(string User, string? Recipient, int Limit)> Asked { get; } = [];

        public List<(string User, string Source, long SourceId, long Until)> Read { get; } = [];

        public Task<IReadOnlyList<InboxThread>> ThreadsAsync(string user, string? recipient, int limit, CancellationToken cancellationToken)
        {
            Asked.Add((user, recipient, limit));
            return Task.FromResult<IReadOnlyList<InboxThread>>([new InboxThread("idea", 4, "💡 Dark mode", "local", 12, "Question", "Create these?", DateTimeOffset.UnixEpoch, 2, 1, true, s_question)]);
        }

        public Task<IReadOnlyList<InboxItem>> ThreadAsync(string source, long sourceId, long? before, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InboxItem>>([s_question]);

        public Task MarkReadAsync(string user, string source, long sourceId, long until, CancellationToken cancellationToken)
        {
            Read.Add((user, source, sourceId, until));
            return Task.CompletedTask;
        }

        public Task<InboxCounts> CountsAsync(string user, CancellationToken cancellationToken) => Task.FromResult(new InboxCounts(1, 2));

        public Task<long> AddAsync(InboxEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
