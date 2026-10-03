using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Messaging.Discord;
using Agentd.Infrastructure.Tests.Messaging.Contract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Infrastructure.Tests.Messaging.Discord;

[TestClass]
public sealed partial class DiscordContractTests : MessagingProviderContractTests
{
    protected override IProviderTestHarness CreateHarness() => new DiscordHarness();

    private sealed partial class DiscordHarness : IProviderTestHarness
    {
        private readonly DiscordProviderTests.FakeDiscord _fake = new();
        private readonly DiscordMessagingProvider _provider;
        private readonly DiscordPoller _poller;
        private readonly Sink _sink = new();
        private readonly List<Conversation> _open = [];

        public DiscordHarness()
        {
            var options = Microsoft.Extensions.Options.Options.Create(new DiscordOptions { Enabled = true, BotToken = "t", GuildId = "777", ChannelId = "555" });
            var http = new HttpClient(_fake) { BaseAddress = new Uri("https://discord.test/api/v10/") };
            var rest = new DiscordRest(() => http);
            _provider = new DiscordMessagingProvider(rest, options);
            _fake.Respond(HttpMethod.Post, "channels/555/messages", """{"id":"starter-1"}""");
            _fake.Respond(HttpMethod.Post, "channels/555/messages/starter-1/threads", """{"id":"t1"}""");
            _fake.Respond(HttpMethod.Post, "channels/t1/messages", """{"id":"m1"}""");
            _fake.Respond(new HttpMethod("PATCH"), "channels/t1/messages/m1", """{"id":"m1"}""");
            _fake.Respond(HttpMethod.Get, "channels/555/messages", "[]");
            var services = new ServiceCollection()
                .AddSingleton<IInboundMessageSink>(_sink)
                .AddSingleton<IConversationStore>(new OpenThreads(_open))
                .BuildServiceProvider();
            _poller = new DiscordPoller(rest, _provider, services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<DiscordPoller>.Instance);
        }

        public IMessagingProvider Provider => _provider;

        public IReadOnlyList<string> WireTexts =>
            _fake.Requests.Where(r => r.Method != HttpMethod.Get && r.Body.StartsWith('{') && r.Path.Contains("/messages", StringComparison.Ordinal) && !r.Path.EndsWith("/threads", StringComparison.Ordinal))
                .Select(r => JsonNode.Parse(r.Body)!["content"]?.GetValue<string>())
                .OfType<string>()
                .ToList();

        public void FailTransport()
        {
            _fake.Respond(HttpMethod.Get, "users/@me", "", HttpStatusCode.ServiceUnavailable);
            _fake.Respond(HttpMethod.Post, "channels/t1/messages", "", HttpStatusCode.ServiceUnavailable);
        }

        public string CommandText(string name, params string[] args) => string.Join(' ', new[] { "!" + name }.Concat(args));

        public async Task<IReadOnlyList<InboundMessage>> DeliverAsync(params NativeMessage[] messages)
        {
            foreach (var thread in messages.Select(m => m.ConversationId).Distinct().Where(t => _open.All(c => c.ExternalConversationId != t)))
            {
                _open.Add(Conversation.Open(new JobId(1), _provider.Key, thread, "777", null, [], DateTimeOffset.UtcNow).Value!);
            }

            foreach (var group in messages.GroupBy(m => m.ConversationId))
            {
                var page = new JsonArray(group.Select(m => (JsonNode)new JsonObject
                {
                    ["id"] = m.Id,
                    ["content"] = m.Text,
                    ["author"] = new JsonObject { ["id"] = "710392908099878953", ["username"] = "dev", ["bot"] = m.FromBot },
                }).Reverse().ToArray());   // Discord returns newest first
                _fake.Respond(HttpMethod.Get, $"channels/{group.Key}/messages", page.ToJsonString());
            }

            var before = _sink.Received.Count;
            await _poller.PollOnceAsync(default);
            return _sink.Received.Skip(before).ToList();
        }

        public IReadOnlyList<string> ActiveMarkup(string wireText)
        {
            var active = new List<string>();
            active.AddRange(MassMention().Matches(wireText).Select(m => $"mass mention '{m.Value}'"));
            active.AddRange(Mention().Matches(wireText).Select(m => $"mention '{m.Value}'"));
            active.AddRange(UnsafeLink().Matches(wireText).Select(m => $"unsafe link '{m.Value}'"));
            active.AddRange(Heading().Matches(wireText).Select(m => $"heading '{m.Value.Trim()}'"));
            return active;
        }

        public IReadOnlyList<string> UnsafeRequests() =>
            _fake.Requests.Where(r => r.Method != HttpMethod.Get && r.Body.StartsWith('{') && r.Path.Contains("/messages", StringComparison.Ordinal) && !r.Path.EndsWith("/threads", StringComparison.Ordinal))
                .Where(r => JsonNode.Parse(r.Body)!["allowed_mentions"]?["parse"]?.AsArray().Count != 0)
                .Select(r => $"{r.Method} {r.Path} without allowed_mentions: {{parse: []}}")
                .ToList();

        public void Dispose() => _fake.Dispose();

        // An active mass mention is '@' directly followed by everyone/here (an escape puts a zero-width space between).
        [GeneratedRegex(@"@(everyone|here)\b", RegexOptions.IgnoreCase)]
        private static partial Regex MassMention();

        [GeneratedRegex(@"<(@[!&]?|#)\d+>")]
        private static partial Regex Mention();

        [GeneratedRegex(@"\]\((?!https?://)[^)]*\)", RegexOptions.IgnoreCase)]
        private static partial Regex UnsafeLink();

        [GeneratedRegex(@"(?m)^\s*-?#{1,3}\s")]
        private static partial Regex Heading();

        private sealed class Sink : IInboundMessageSink
        {
            public List<InboundMessage> Received { get; } = [];

            public Task HandleAsync(InboundMessage message, CancellationToken cancellationToken)
            {
                Received.Add(message);
                return Task.CompletedTask;
            }
        }

        private sealed class OpenThreads(List<Conversation> open) : IConversationStore
        {
            public Task<IReadOnlyList<Conversation>> ListOpenAsync(ProviderKey provider, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<Conversation>>(open.ToList());

            public Task<Domain.Common.Result> AddAsync(Conversation c, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<Domain.Common.Result> SaveAsync(Conversation c, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<IReadOnlyList<Conversation>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<Conversation?> FindExternalAsync(ProviderKey provider, string externalConversationId, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<IReadOnlyList<Conversation>> ListOpenByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<Domain.Common.Result> MoveAsync(Conversation conversation, JobId jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
