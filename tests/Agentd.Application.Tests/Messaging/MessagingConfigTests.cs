using Agentd.Application.Messaging;
using Agentd.Application.Users;
using Agentd.Domain.Messaging;
using Agentd.Domain.Users;

namespace Agentd.Application.Tests.Messaging;

[TestClass]
public sealed class MessagingConfigTests
{
    [TestMethod]
    [DataRow("approve")]
    [DataRow("opt_1-B")]
    [DataRow("abcdefghijklmnopqrstuvwxyz012345")]
    public void Valid_option_ids(string id) => Assert.IsTrue(MessageOption.Create(id, "Label").IsSuccess);

    [TestMethod]
    [DataRow("")]
    [DataRow("abcdefghijklmnopqrstuvwxyz0123456")]
    [DataRow("has space")]
    [DataRow("émoji")]
    [DataRow("a:b")]
    public void Invalid_option_ids_are_rejected(string id) => Assert.IsFalse(MessageOption.Create(id, "Label").IsSuccess);

    [TestMethod]
    public void A_message_needs_text_or_an_attachment()
    {
        Assert.IsFalse(OutboundMessage.Create(MessageKind.Info, " ").IsSuccess);
        Assert.IsTrue(OutboundMessage.Create(MessageKind.Info, "hi").IsSuccess);
        Assert.IsTrue(OutboundMessage.Create(MessageKind.Info, null, attachments: [new Attachment("log.txt", "text/plain", new byte[] { 1 })]).IsSuccess);
    }

    [TestMethod]
    public void Without_defaults_every_enabled_provider_is_used()
    {
        var resolver = Resolver(enabled: ["Discord", "Slack"], disabled: ["Telegram"]);

        var targets = resolver.Resolve(["ai-workflow"]);

        CollectionAssert.AreEqual(new[] { "discord", "slack" }, targets.Providers.Select(p => p.Value).ToArray());
        Assert.IsEmpty(targets.IgnoredTags);
    }

    [TestMethod]
    public void Default_providers_limit_the_choice()
    {
        var resolver = Resolver(enabled: ["Discord", "Slack"], defaults: ["slack"]);

        CollectionAssert.AreEqual(new[] { "slack" }, resolver.Resolve([]).Providers.Select(p => p.Value).ToArray());
    }

    [TestMethod]
    public void Chat_tags_override_and_unknown_ones_are_reported()
    {
        var resolver = Resolver(enabled: ["Discord", "Slack"], disabled: ["Telegram"], defaults: ["slack"]);

        var targets = resolver.Resolve(["chat:Discord", "chat:telegram", "chat:nope", "repo:sysmin"]);

        CollectionAssert.AreEqual(new[] { "discord" }, targets.Providers.Select(p => p.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "chat:telegram", "chat:nope" }, targets.IgnoredTags.ToArray());
    }

    [TestMethod]
    public void Only_unknown_chat_tags_fall_back_to_the_defaults()
    {
        var resolver = Resolver(enabled: ["Discord"]);

        CollectionAssert.AreEqual(new[] { "discord" }, resolver.Resolve(["chat:telegram"]).Providers.Select(p => p.Value).ToArray());
    }

    [TestMethod]
    public void Messaging_validation_rejects_defaults_that_are_not_enabled()
    {
        var o = Options(enabled: ["Discord"], disabled: ["Telegram"], defaults: ["telegram", "discord"]);

        var result = new MessagingOptionsValidator().Validate(null, o);

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "'telegram' is not an enabled provider");
    }

    [TestMethod]
    public void User_validation_rejects_duplicates()
    {
        var o = new UsersOptions();
        o.Items.Add(User("alice", ("Discord", "1")));
        o.Items.Add(User("Alice", ("Discord", "2")));
        o.Items.Add(User("bob", ("discord", "1")));
        o.Items.Add(User("carol", ("Bad Name", "3")));

        var result = new UsersOptionsValidator().Validate(null, o);

        StringAssert.Contains(result.FailureMessage, "'Alice' is used twice");
        StringAssert.Contains(result.FailureMessage, "mapped to both 'alice' and 'bob'");
        StringAssert.Contains(result.FailureMessage, "identity 'Bad Name'");
    }

    [TestMethod]
    public async Task Seeding_syncs_the_configured_users()
    {
        var o = new UsersOptions();
        o.Items.Add(User("tngo", ("Discord", " 789 ")));
        var directory = new FakeDirectory();

        var count = await new SeedUsersHandler(directory, Microsoft.Extensions.Options.Options.Create(o)).Handle(new SeedUsers(), default);

        Assert.AreEqual(1, count.Value);
        var identity = directory.Synced!.Single().Identities.Single();
        Assert.AreEqual(ProviderKey.From("discord"), identity.Provider);
        Assert.AreEqual("789", identity.ExternalId);
    }

    private static UserSeed User(string name, (string Provider, string Id) identity)
    {
        var seed = new UserSeed { Name = name };
        seed.Identities[identity.Provider] = identity.Id;
        return seed;
    }

    private static ConversationTargetsResolver Resolver(string[] enabled, string[]? disabled = null, string[]? defaults = null) =>
        new(Microsoft.Extensions.Options.Options.Create(Options(enabled, disabled, defaults)));

    private static MessagingOptions Options(string[] enabled, string[]? disabled = null, string[]? defaults = null)
    {
        var o = new MessagingOptions();
        foreach (var name in enabled)
        {
            o.Providers[name] = new MessagingProviderSettings { Enabled = true };
        }

        foreach (var name in disabled ?? [])
        {
            o.Providers[name] = new MessagingProviderSettings { Enabled = false };
        }

        foreach (var name in defaults ?? [])
        {
            o.DefaultProviders.Add(name);
        }

        return o;
    }

    private sealed class FakeDirectory : IUserDirectory
    {
        public IReadOnlyList<User>? Synced { get; private set; }

        public Task<AgentdUser?> FindByIdentityAsync(ProviderKey provider, string externalId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentdUser?>(null);

        public Task SyncAsync(IReadOnlyList<User> users, CancellationToken cancellationToken)
        {
            Synced = users;
            return Task.CompletedTask;
        }
    }
}
