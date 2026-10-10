using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Messaging.Discord;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Infrastructure.Tests.Messaging.Discord;

[TestClass]
public sealed class DiscordPollerTests : IDisposable
{
    private readonly DiscordProviderTests.FakeDiscord _fake = new();

    public void Dispose() => _fake.Dispose();

    [TestMethod]
    public async Task Replies_in_an_idea_thread_are_read_like_replies_in_a_job_thread()
    {
        _fake.Respond(HttpMethod.Get, "channels/555/messages", "[]");
        _fake.Respond(HttpMethod.Get, "channels/job-t/messages", """[{"id":"11","content":"looks good","author":{"id":"7","username":"dev"}}]""");
        _fake.Respond(HttpMethod.Get, "channels/idea-t/messages", """[{"id":"12","content":"only the admin pages","author":{"id":"7","username":"dev"}}]""");
        var sink = new Sink();
        var services = new ServiceCollection()
            .AddSingleton<IInboundMessageSink>(sink)
            .AddSingleton<IConversationStore>(new JobThreads("job-t"))
            .AddSingleton<IIdeaStore>(new IdeaThreads("idea-t", "job-t"))   // a duplicate id is read once
            .BuildServiceProvider();
        var options = Microsoft.Extensions.Options.Options.Create(new DiscordOptions { Enabled = true, BotToken = "t", GuildId = "777", ChannelId = "555" });
        var rest = new DiscordRest(() => new HttpClient(_fake) { BaseAddress = new Uri("https://discord.test/api/v10/") });
        var poller = new DiscordPoller(rest, new DiscordMessagingProvider(rest, options), services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<DiscordPoller>.Instance);

        await poller.PollOnceAsync(default);

        CollectionAssert.AreEquivalent(new[] { "job-t: looks good", "idea-t: only the admin pages" },
            sink.Received.Select(m => $"{m.ExternalConversationId}: {m.Text}").ToArray());
        Assert.AreEqual(1, _fake.RequestUris.Count(u => u.Contains("channels/job-t/messages", StringComparison.Ordinal)));
    }

    private sealed class Sink : IInboundMessageSink
    {
        public List<InboundMessage> Received { get; } = [];

        public Task HandleAsync(InboundMessage message, CancellationToken cancellationToken)
        {
            Received.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class IdeaThreads(params string[] threads) : IIdeaStore
    {
        public Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(threads);

        public Task<long> InsertAsync(string repository, string title, string author, ProviderKey provider, string? threadId, string? spaceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Idea?> GetAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Idea?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveAsync(Idea idea, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddMessageAsync(long ideaId, string direction, string author, string text, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long ideaId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<IdeaSummary>> ListSummariesAsync(long? id, int? workItem, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class JobThreads(string thread) : IConversationStore
    {
        public Task<IReadOnlyList<Conversation>> ListOpenAsync(ProviderKey provider, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Conversation>>([Conversation.Open(new JobId(1), provider, thread, "777", null, [], DateTimeOffset.UtcNow).Value!]);

        public Task<Domain.Common.Result> AddAsync(Conversation c, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Domain.Common.Result> SaveAsync(Conversation c, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Conversation?> FindExternalAsync(ProviderKey provider, string externalConversationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> ListOpenByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Domain.Common.Result> MoveAsync(Conversation conversation, JobId jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
