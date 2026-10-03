using Agentd.Application.Messaging;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Tests.Messaging;

[TestClass]
public sealed class RegistryAndCatalogTests
{
    [TestMethod]
    public void Only_enabled_providers_in_configuration_order()
    {
        var options = new MessagingOptions();
        options.Providers["Slack"] = new MessagingProviderSettings { Enabled = true };
        options.Providers["Discord"] = new MessagingProviderSettings { Enabled = true };
        options.Providers["Telegram"] = new MessagingProviderSettings { Enabled = false };
        IMessagingProvider[] registered = [new StubProvider("discord"), new StubProvider("telegram"), new StubProvider("slack")];

        var registry = new MessagingProviderRegistry(registered, Microsoft.Extensions.Options.Options.Create(options));

        CollectionAssert.AreEqual(new[] { "slack", "discord" }, registry.Enabled.Select(p => p.Key.Value).ToArray());
        Assert.AreEqual("discord", registry.Resolve(ProviderKey.From("discord")).Key.Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve(ProviderKey.From("telegram")));
    }

    [TestMethod]
    public void Catalog_passes_text_through_as_data()
    {
        var question = MessageCatalog.Question("Use <b>v2</b> or @everyone?", [new MessageOption("v2", "v2")]);

        Assert.AreEqual(MessageKind.Question, question.Kind);
        StringAssert.Contains(question.Markdown, "Use <b>v2</b> or @everyone?");
        Assert.HasCount(1, question.Options!);
        StringAssert.Contains(MessageCatalog.PullRequestReady(new Uri("https://dev.azure.com/o/p/_git/r/pullrequest/7"), "Fixed").Markdown, "pullrequest/7");
        Assert.AreEqual(MessageKind.Error, MessageCatalog.Failed("boom").Kind);
    }

    private sealed class StubProvider(string key) : IMessagingProvider
    {
        public ProviderKey Key { get; } = ProviderKey.From(key);

        public MessagingCapabilities Capabilities { get; } = new(2000, true, true, true, true);

        public Task<ConversationRef> OpenConversationAsync(ConversationSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageRef> SendAsync(ConversationRef conversation, OutboundMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task EditAsync(MessageRef message, OutboundMessage replacement, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CloseConversationAsync(ConversationRef conversation, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(MessageRef message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteConversationAsync(ConversationRef conversation, CancellationToken cancellationToken) => Task.CompletedTask;

        public Uri? GetLink(ConversationRef conversation) => null;

        public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
