using Agentd.Application.Messaging;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Infrastructure.Tests.Messaging.Contract;

/// <summary>
/// The contract every messaging provider passes (docs/architect/messaging-providers.md §9). A provider's
/// test class derives from this and supplies a harness; capability-dependent cases report an explicit
/// Inconclusive when the capability is off.
/// </summary>
public abstract class MessagingProviderContractTests
{
    private static readonly Lazy<string[]> s_hostile = new(() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Messaging", "Contract", "hostile-inputs.txt"))
            .Where(l => l.Length > 0).ToArray());

    protected abstract IProviderTestHarness CreateHarness();

    [TestMethod]
    public async Task OpenConversation_returns_ref_and_link()
    {
        using var h = CreateHarness();

        var conversation = await h.Provider.OpenConversationAsync(Spec("hello"), default);

        Assert.AreEqual(h.Provider.Key, conversation.Provider);
        Assert.IsFalse(string.IsNullOrWhiteSpace(conversation.ExternalConversationId));
        if (h.Provider.GetLink(conversation) is { } link)
        {
            Assert.IsTrue(link.IsAbsoluteUri && link.Scheme == "https", $"link {link}");
        }
    }

    [TestMethod]
    public async Task Send_respects_MaxMessageLength_after_rendering()
    {
        using var h = CreateHarness();
        var thread = await h.Provider.OpenConversationAsync(Spec("start"), default);
        var max = h.Provider.Capabilities.MaxMessageLength;

        foreach (var input in s_hostile.Value.Append(string.Join("\n\n", s_hostile.Value)))
        {
            foreach (var part in MessageChunker.Prepare(new OutboundMessage(MessageKind.Info, input), h.Provider.Capabilities with { SupportsAttachments = false }))
            {
                await h.Provider.SendAsync(thread, part, default);
            }
        }

        var tooLong = h.WireTexts.Where(t => t.Length > max).Select(t => t.Length).ToList();
        Assert.IsEmpty(tooLong, $"rendered messages over {max}: {string.Join(", ", tooLong)}");
    }

    [TestMethod]
    public async Task Hostile_text_is_inert()
    {
        using var h = CreateHarness();
        var thread = await h.Provider.OpenConversationAsync(Spec("start"), default);
        var failures = new List<string>();

        foreach (var input in s_hostile.Value)
        {
            var before = h.WireTexts.Count;
            foreach (var part in MessageChunker.Prepare(new OutboundMessage(MessageKind.Info, $"intro\n{input}\nend {input}"), h.Provider.Capabilities with { SupportsAttachments = false }))
            {
                await h.Provider.SendAsync(thread, part, default);
            }

            failures.AddRange(h.WireTexts.Skip(before).SelectMany(h.ActiveMarkup).Select(a => $"{Short(input)} → {a}"));
        }

        failures.AddRange(h.UnsafeRequests());
        Assert.IsEmpty(failures, string.Join("\n", failures));
    }

    [TestMethod]
    public async Task Options_round_trip()
    {
        using var h = CreateHarness();
        var thread = await h.Provider.OpenConversationAsync(Spec("start"), default);
        MessageOption[] options = [new("opt1", "Use v1"), new("opt2", "Use v2")];

        await h.Provider.SendAsync(thread, new OutboundMessage(MessageKind.Question, "Which?", options), default);
        var received = await h.DeliverAsync(new NativeMessage("901", thread.ExternalConversationId, h.Provider.Capabilities.SupportsOptions ? "opt2" : "2"));

        var answer = received.Single();
        Assert.AreEqual("opt2", answer.SelectedOptionId);
        Assert.AreEqual("Use v2", answer.Text);
    }

    [TestMethod]
    public async Task Inbound_from_bot_is_ignored()
    {
        using var h = CreateHarness();
        var thread = await h.Provider.OpenConversationAsync(Spec("start"), default);

        var received = await h.DeliverAsync(new NativeMessage("902", thread.ExternalConversationId, "I am a bot", FromBot: true));

        Assert.IsEmpty(received);
    }

    [TestMethod]
    public async Task Inbound_duplicate_keeps_its_id()
    {
        using var h = CreateHarness();
        var thread = await h.Provider.OpenConversationAsync(Spec("start"), default);
        var message = new NativeMessage("903", thread.ExternalConversationId, "hello");

        var received = (await h.DeliverAsync(message)).Concat(await h.DeliverAsync(message)).ToList();

        Assert.IsNotEmpty(received);
        Assert.IsTrue(received.All(m => m.ExternalMessageId == "903"), "redeliveries carry the same id, so the Application layer can dedupe");
    }

    [TestMethod]
    public async Task Commands_normalize()
    {
        using var h = CreateHarness();
        var thread = await h.Provider.OpenConversationAsync(Spec("start"), default);

        var received = await h.DeliverAsync(
            new NativeMessage("904", thread.ExternalConversationId, h.CommandText("status")),
            new NativeMessage("905", thread.ExternalConversationId, h.CommandText("cancel")),
            new NativeMessage("906", thread.ExternalConversationId, h.CommandText("run", "1234")));

        CollectionAssert.AreEqual(new[] { "status", "cancel", "run 1234" },
            received.Select(m => string.Join(' ', new[] { m.Command!.Name }.Concat(m.Command.Args))).ToArray());
    }

    [TestMethod]
    public async Task Edit_updates_existing_message()
    {
        using var h = CreateHarness();
        if (!h.Provider.Capabilities.SupportsEditing)
        {
            Assert.Inconclusive($"{h.Provider.Key} does not support editing.");
        }

        var thread = await h.Provider.OpenConversationAsync(Spec("start"), default);
        var sent = await h.Provider.SendAsync(thread, new OutboundMessage(MessageKind.Progress, "step 1"), default);

        await h.Provider.EditAsync(sent, new OutboundMessage(MessageKind.Progress, "step 2"), default);

        Assert.AreEqual("step 2", h.WireTexts[^1]);
    }

    [TestMethod]
    public async Task Health_reports_down_when_transport_fails()
    {
        using var h = CreateHarness();
        h.FailTransport();

        var health = await h.Provider.CheckHealthAsync(default);

        Assert.IsFalse(health.Healthy);
        Assert.IsFalse(string.IsNullOrWhiteSpace(health.Detail));
    }

    private static ConversationSpec Spec(string opening) =>
        new(new JobId(1), WorkItemId.From(1234), "Fix login", RepositoryName.From("sysmin"), new OutboundMessage(MessageKind.Info, opening));

    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + $"… ({s.Length} chars)";
}
