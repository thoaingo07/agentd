using Agentd.Application.Messaging;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Persistence.Messaging;
using Agentd.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class OutboxDeliveryTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private static int s_next;

    [TestMethod]
    public async Task Each_conversation_delivers_in_order_one_row_at_a_time()
    {
        await using var db = await Database.CreateMigratedAsync(NextName());
        var a = await JobWithConversationAsync(db);
        var b = await JobWithConversationAsync(db);
        await EnqueueAsync(db, a, "a1", "a2");
        await EnqueueAsync(db, b, "b1");
        var delivery = new OutboxDelivery(db);

        var first = await delivery.ClaimAsync(50, [s_discord], default);
        CollectionAssert.AreEquivalent(new[] { "a1", "b1" }, first.Select(i => i.Message.Markdown).ToArray());
        Assert.IsEmpty(await delivery.ClaimAsync(50, [s_discord], default), "a2 waits for a1");

        await delivery.MarkSentAsync(first.Single(i => i.Message.Markdown == "a1").Id, "ext-1", false, default);
        Assert.AreEqual("a2", (await delivery.ClaimAsync(50, [s_discord], default)).Single().Message.Markdown);
        Assert.IsEmpty(await delivery.ClaimAsync(50, [ProviderKey.From("slack")], default), "other providers' rows only");
    }

    [TestMethod]
    public async Task Parallel_claimers_never_get_the_same_row()
    {
        await using var db = await Database.CreateMigratedAsync(NextName());
        for (var i = 0; i < 20; i++)
        {
            await EnqueueAsync(db, await JobWithConversationAsync(db), $"m{i}");
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => new OutboxDelivery(db).ClaimAsync(10, [s_discord], default)));

        var ids = claims.SelectMany(c => c).Select(i => i.Id).ToList();
        Assert.HasCount(20, ids);
        Assert.HasCount(20, ids.Distinct());
    }

    [TestMethod]
    public async Task Retries_end_dead_and_failures_are_recorded_on_the_job()
    {
        await using var db = await Database.CreateMigratedAsync(NextName());
        var job = await JobWithConversationAsync(db);
        await EnqueueAsync(db, job, "retry", "fail");
        var delivery = new OutboxDelivery(db);

        var row = (await delivery.ClaimAsync(50, [s_discord], default)).Single();
        Assert.IsTrue(await delivery.MarkRetryAsync(row.Id, "503", DateTimeOffset.UtcNow.AddMinutes(-1), maxAttempts: 2, default));
        row = (await delivery.ClaimAsync(50, [s_discord], default)).Single();
        Assert.AreEqual(1, row.Attempts);
        Assert.IsFalse(await delivery.MarkRetryAsync(row.Id, "503", DateTimeOffset.UtcNow, maxAttempts: 2, default), "dead after 2 attempts");

        var next = (await delivery.ClaimAsync(50, [s_discord], default)).Single();
        Assert.AreEqual("fail", next.Message.Markdown, "a dead row no longer blocks the conversation");
        await delivery.MarkFailedAsync(next.Id, "thread deleted", default);

        CollectionAssert.AreEquivalent(new[] { "MessagingDeliveryDead", "MessagingDeliveryFailed" }, await EventTypesAsync(db, job, "MessagingDelivery%"));
    }

    [TestMethod]
    public async Task A_new_status_message_is_remembered_and_stale_claims_are_released()
    {
        await using var db = await Database.CreateMigratedAsync(NextName());
        var job = await JobWithConversationAsync(db);
        await new Outbox(db).EnqueueAsync(job, [new OutboxMessage(MessageCatalog.Progress("p1"), new EnqueueOptions(ReplaceStatusMessage: true))], default);
        var delivery = new OutboxDelivery(db);
        var row = (await delivery.ClaimAsync(50, [s_discord], default)).Single();
        Assert.IsTrue(row.ReplaceStatusMessage);
        await delivery.MarkSentAsync(row.Id, "status-1", createdStatusMessage: true, default);

        await EnqueueAsync(db, job, "stuck");
        _ = await delivery.ClaimAsync(50, [s_discord], default);   // claimed, then the dispatcher "crashes"
        Assert.AreEqual(0, await delivery.ReleaseStaleAsync(DateTimeOffset.UtcNow.AddMinutes(-2), default), "not stale yet");
        Assert.AreEqual(1, await delivery.ReleaseStaleAsync(DateTimeOffset.UtcNow.AddSeconds(1), default));

        var again = (await delivery.ClaimAsync(50, [s_discord], default)).Single();
        Assert.AreEqual("stuck", again.Message.Markdown);
        Assert.AreEqual("status-1", again.StatusMessageId);
    }

    [TestMethod]
    public async Task After_an_outage_every_message_arrives_in_order()
    {
        await using var db = await Database.CreateMigratedAsync(NextName());
        var job = await JobWithConversationAsync(db);
        await EnqueueAsync(db, job, "m1", "m2", "m3");
        var chat = new FlakyChat { FailuresLeft = 7 };
        var options = new MessagingOptions();
        options.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        // The dispatcher's clock runs an hour behind, so its back-off times are already due for the database.
        var clock = new ShiftedClock(TimeSpan.FromHours(-1));
        var dispatcher = new OutboxDispatcher(new OutboxDelivery(db), new MessagingProviderRegistry([chat], Microsoft.Extensions.Options.Options.Create(options)), clock);

        for (var round = 0; round < 30 && chat.Delivered.Count < 3; round++)
        {
            await dispatcher.DispatchOnceAsync(50, default);
            clock.Shift += OutboxDispatcher.CircuitOpenFor;   // let an open circuit close again
        }

        CollectionAssert.AreEqual(new[] { "m1", "m2", "m3" }, chat.Delivered);
    }

    private static string NextName() => $"outbox_delivery_{Interlocked.Increment(ref s_next)}";

    private static Task EnqueueAsync(NpgsqlDataSource db, JobId job, params string[] texts) =>
        new Outbox(db).EnqueueAsync(job, texts.Select(t => new OutboxMessage(new OutboundMessage(MessageKind.Info, t))).ToList(), default);

    private static async Task<JobId> JobWithConversationAsync(NpgsqlDataSource db)
    {
        var clock = new Clock();
        var job = Job.Create(WorkItemId.From(Interlocked.Increment(ref s_next) + 100), RepositoryName.From("sysmin"), "t", clock);
        await new JobRepository(db, clock).AddAsync(job, default);
        await new ConversationStore(db).AddAsync(Conversation.Open(job.Id, s_discord, $"thread-{job.Id}", null, null, [], clock.UtcNow).Value!, default);
        return job.Id;
    }

    private static async Task<List<string>> EventTypesAsync(NpgsqlDataSource db, JobId job, string like)
    {
        await using var cmd = db.CreateCommand("SELECT type FROM agentd.events WHERE job_id = $1 AND type LIKE $2");
        cmd.Parameters.Add(new NpgsqlParameter { Value = job.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = like });
        await using var r = await cmd.ExecuteReaderAsync();
        var types = new List<string>();
        while (await r.ReadAsync())
        {
            types.Add(r.GetString(0));
        }

        return types;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class ShiftedClock(TimeSpan shift) : IClock
    {
        public TimeSpan Shift { get; set; } = shift;

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Shift;
    }

    private sealed class FlakyChat : IMessagingProvider
    {
        public int FailuresLeft { get; set; }

        public List<string> Delivered { get; } = [];

        public ProviderKey Key => s_discord;

        public MessagingCapabilities Capabilities { get; } = new(2000, true, true, true, true);

        public Task<MessageRef> SendAsync(ConversationRef conversation, OutboundMessage message, CancellationToken cancellationToken)
        {
            if (FailuresLeft-- > 0)
            {
                throw new HttpRequestException("network down");
            }

            Delivered.Add(message.Markdown);
            return Task.FromResult(new MessageRef(conversation, $"ext-{Delivered.Count}"));
        }

        public Task EditAsync(MessageRef message, OutboundMessage replacement, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ConversationRef> OpenConversationAsync(ConversationSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CloseConversationAsync(ConversationRef conversation, string reason, CancellationToken cancellationToken) => Task.CompletedTask;

        public Uri? GetLink(ConversationRef conversation) => null;

        public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new ProviderHealth(true, "ok"));
    }
}
