using Agentd.Application.Messaging;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Tests.Messaging;

[TestClass]
public sealed class OutboxDispatcherTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private readonly FakeClock _clock = new();
    private readonly FakeDelivery _delivery = new();
    private readonly ScriptedChat _chat = new();

    [TestMethod]
    public async Task A_long_message_goes_out_in_parts_and_the_first_id_is_kept()
    {
        _delivery.Pending.Add(Item(1, new OutboundMessage(MessageKind.Info, string.Join("\n\n", Enumerable.Range(0, 4).Select(_ => new string('x', 900))))));

        await Dispatcher().DispatchOnceAsync(50, default);

        Assert.HasCount(2, _chat.Sent);
        Assert.AreEqual((1L, "msg-1", false), _delivery.SentRows.Single());
    }

    [TestMethod]
    public async Task Progress_creates_then_edits_the_status_message()
    {
        _delivery.Pending.Add(Item(1, MessageCatalog.Progress("step 1"), replace: true));
        _delivery.Pending.Add(Item(2, MessageCatalog.Progress("step 2"), replace: true, statusMessageId: "status-9"));

        await Dispatcher().DispatchOnceAsync(50, default);

        Assert.AreEqual("step 1", _chat.Sent.Single().Markdown);
        Assert.AreEqual(("status-9", "step 2"), _chat.Edited.Single());
        CollectionAssert.AreEquivalent(new[] { (1L, "msg-1", true), (2L, "status-9", false) }, _delivery.SentRows);
    }

    [TestMethod]
    public async Task Transient_failures_back_off_and_429_waits_as_told()
    {
        _chat.Throw = new HttpRequestException("503");
        _delivery.Pending.Add(Item(1, Hello(), attempts: 3));

        await Dispatcher().DispatchOnceAsync(50, default);

        var retry = _delivery.Retries.Single();
        Assert.IsTrue(retry.At >= _clock.UtcNow.AddSeconds(8) && retry.At < _clock.UtcNow.AddSeconds(9), $"2^3 s plus jitter, was {retry.At - _clock.UtcNow}");

        _chat.Throw = new MessagingDeliveryException("rate limited", permanent: false, retryAfter: TimeSpan.FromSeconds(42));
        _delivery.Pending.Add(Item(2, Hello()));
        await Dispatcher().DispatchOnceAsync(50, default);
        Assert.AreEqual(_clock.UtcNow.AddSeconds(42), _delivery.Retries[1].At);
    }

    [TestMethod]
    public async Task Permanent_failures_are_not_retried()
    {
        _chat.Throw = new MessagingDeliveryException("thread deleted", permanent: true);
        _delivery.Pending.Add(Item(1, Hello()));

        await Dispatcher().DispatchOnceAsync(50, default);

        Assert.AreEqual((1L, "thread deleted"), _delivery.Failed.Single());
        Assert.IsEmpty(_delivery.Retries);
    }

    [TestMethod]
    public async Task Repeated_failures_pause_the_provider_for_a_minute()
    {
        var dispatcher = Dispatcher();
        _chat.Throw = new HttpRequestException("timeout");
        for (var i = 1; i <= OutboxDispatcher.CircuitThreshold; i++)
        {
            _delivery.Pending.Add(Item(i, Hello()));
            await dispatcher.DispatchOnceAsync(50, default);
        }

        CollectionAssert.AreEqual(new[] { s_discord }, dispatcher.PausedProviders.ToArray());
        _delivery.Pending.Add(Item(99, Hello()));
        Assert.AreEqual(0, await dispatcher.DispatchOnceAsync(50, default), "paused providers are not claimed");

        _clock.UtcNow += OutboxDispatcher.CircuitOpenFor;
        _chat.Throw = null;
        Assert.AreEqual(1, await dispatcher.DispatchOnceAsync(50, default));
        Assert.IsEmpty(dispatcher.PausedProviders);
    }

    private OutboxDispatcher Dispatcher()
    {
        var options = new MessagingOptions();
        options.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        return new OutboxDispatcher(_delivery, new MessagingProviderRegistry([_chat], Microsoft.Extensions.Options.Options.Create(options)), _clock);
    }

    private static OutboundMessage Hello() => new(MessageKind.Info, "hello");

    private static OutboxItem Item(long id, OutboundMessage message, bool replace = false, string? statusMessageId = null, int attempts = 0) =>
        new(id, new JobId(1), new ConversationRef(s_discord, $"thread-{id}", null), message, replace, statusMessageId, attempts);

    private sealed class FakeDelivery : IOutboxDelivery
    {
        public List<OutboxItem> Pending { get; } = [];

        public List<(long Id, string ExternalId, bool Status)> SentRows { get; } = [];

        public List<(long Id, DateTimeOffset At)> Retries { get; } = [];

        public List<(long Id, string Reason)> Failed { get; } = [];

        public Task<IReadOnlyList<OutboxItem>> ClaimAsync(int limit, IReadOnlyList<ProviderKey> providers, CancellationToken cancellationToken)
        {
            var claimed = Pending.Where(i => providers.Contains(i.Conversation.Provider)).Take(limit).ToList();
            Pending.RemoveAll(claimed.Contains);
            return Task.FromResult<IReadOnlyList<OutboxItem>>(claimed);
        }

        public Task MarkSentAsync(long id, string externalMessageId, bool createdStatusMessage, CancellationToken cancellationToken)
        {
            lock (SentRows)
            {
                SentRows.Add((id, externalMessageId, createdStatusMessage));
            }

            return Task.CompletedTask;
        }

        public Task<bool> MarkRetryAsync(long id, string reason, DateTimeOffset nextAttemptAt, int maxAttempts, CancellationToken cancellationToken)
        {
            Retries.Add((id, nextAttemptAt));
            return Task.FromResult(true);
        }

        public Task MarkFailedAsync(long id, string reason, CancellationToken cancellationToken)
        {
            Failed.Add((id, reason));
            return Task.CompletedTask;
        }

        public Task<int> ReleaseStaleAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class ScriptedChat : IMessagingProvider
    {
        private int _next;

        public ProviderKey Key => s_discord;

        public MessagingCapabilities Capabilities { get; } = new(2000, true, true, false, true);

        public Exception? Throw { get; set; }

        public List<OutboundMessage> Sent { get; } = [];

        public List<(string Id, string Markdown)> Edited { get; } = [];

        public Task<MessageRef> SendAsync(ConversationRef conversation, OutboundMessage message, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            lock (Sent)
            {
                Sent.Add(message);
                return Task.FromResult(new MessageRef(conversation, $"msg-{++_next}"));
            }
        }

        public Task EditAsync(MessageRef message, OutboundMessage replacement, CancellationToken cancellationToken)
        {
            lock (Edited)
            {
                Edited.Add((message.ExternalMessageId, replacement.Markdown));
            }

            return Task.CompletedTask;
        }

        public Task<ConversationRef> OpenConversationAsync(ConversationSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CloseConversationAsync(ConversationRef conversation, string reason, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(MessageRef message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteConversationAsync(ConversationRef conversation, CancellationToken cancellationToken) => Task.CompletedTask;

        public Uri? GetLink(ConversationRef conversation) => null;

        public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new ProviderHealth(true, "ok"));
    }
}
