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
public sealed class OutboxTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private static readonly ProviderKey s_slack = ProviderKey.From("slack");
    private static NpgsqlDataSource s_db = null!;
    private static int s_nextWorkItem = 7000;
    private readonly Clock _clock = new();

    [ClassInitialize]
    public static async Task InitAsync(TestContext _) => s_db = await Database.CreateMigratedAsync("outbox");

    [ClassCleanup]
    public static async Task CleanupAsync() => await s_db.DisposeAsync();

    [TestMethod]
    public async Task A_state_change_writes_its_message_to_every_open_conversation()
    {
        var job = await RunningJobWithConversationsAsync(s_discord, s_slack);

        job.Fail("The agent exited (code 1) without calling finish.");
        Assert.IsTrue((await new JobRepository(s_db, _clock).SaveAsync(job, default)).IsSuccess);

        var rows = await RowsAsync(job.Id);
        CollectionAssert.AreEquivalent(new[] { "discord", "slack" }, rows.Select(r => r.Provider).ToArray());
        Assert.IsTrue(rows.All(r => r.Kind == "Error" && r.Status == "pending"));
        var (message, _) = OutboxPayload.Read(rows[0].Payload);
        StringAssert.Contains(message.Markdown, "without calling finish");
    }

    [TestMethod]
    public async Task A_rejected_save_writes_no_messages()
    {
        var job = await RunningJobWithConversationsAsync(s_discord);
        var stale = (await new JobRepository(s_db, _clock).GetAsync(job.Id, default))!;
        job.Cancel("tngo");
        await new JobRepository(s_db, _clock).SaveAsync(job, default);

        stale.Fail("late");
        var result = await new JobRepository(s_db, _clock).SaveAsync(stale, default);

        Assert.AreEqual("conflict", result.Error?.Code);
        CollectionAssert.AreEqual(new[] { "Info" }, (await RowsAsync(job.Id)).Select(r => r.Kind).ToArray(), "only the cancel message");
    }

    [TestMethod]
    public async Task Rapid_progress_updates_keep_only_the_newest_pending_row()
    {
        var job = await RunningJobWithConversationsAsync(s_discord);
        var outbox = new Outbox(s_db);

        for (var i = 1; i <= 5; i++)
        {
            await outbox.EnqueueAsync(job.Id, [new OutboxMessage(MessageCatalog.Progress($"step {i}"), new EnqueueOptions(ReplaceStatusMessage: true))], default);
        }

        var row = (await RowsAsync(job.Id)).Single();
        var (message, replace) = OutboxPayload.Read(row.Payload);
        Assert.AreEqual("step 5", message.Markdown);
        Assert.IsTrue(replace);
    }

    [TestMethod]
    public async Task Only_and_except_filter_the_conversations()
    {
        var job = await RunningJobWithConversationsAsync(s_discord, s_slack);
        var outbox = new Outbox(s_db);

        await outbox.EnqueueAsync(job.Id, [new OutboxMessage(new OutboundMessage(MessageKind.Info, "mirrored", [new MessageOption("a", "A")]), new EnqueueOptions(ExceptProviders: [s_discord]))], default);
        await outbox.EnqueueAsync(job.Id, [new OutboxMessage(new OutboundMessage(MessageKind.Info, "only"), new EnqueueOptions(OnlyProviders: [s_discord]))], default);

        var rows = await RowsAsync(job.Id);
        Assert.AreEqual("slack", rows.Single(r => OutboxPayload.Read(r.Payload).Message.Markdown == "mirrored").Provider);
        Assert.AreEqual("discord", rows.Single(r => OutboxPayload.Read(r.Payload).Message.Markdown == "only").Provider);
        Assert.AreEqual("a", OutboxPayload.Read(rows[0].Payload).Message.Options!.Single().Id);
    }

    private async Task<Job> RunningJobWithConversationsAsync(params ProviderKey[] providers)
    {
        var repo = new JobRepository(s_db, _clock);
        var job = Job.Create(WorkItemId.From(Interlocked.Increment(ref s_nextWorkItem)), RepositoryName.From("sysmin"), "t", _clock);
        await repo.AddAsync(job, default);
        job.BeginPreparing();
        job.Start(new WorktreePath("/wt"), BranchName.From($"ai/{job.WorkItemId}-t"), ClaudeSessionId.New());
        await repo.SaveAsync(job, default);
        foreach (var provider in providers)
        {
            await new ConversationStore(s_db).AddAsync(Conversation.Open(job.Id, provider, $"{provider}-{job.Id}", null, null, [], _clock.UtcNow).Value!, default);
        }

        return job;
    }

    private static async Task<List<(string Provider, string Kind, string Status, string Payload)>> RowsAsync(JobId job)
    {
        await using var cmd = s_db.CreateCommand("SELECT provider, kind, status, payload::text FROM agentd.outbound_messages WHERE job_id = $1 ORDER BY id");
        cmd.Parameters.Add(new NpgsqlParameter { Value = job.Value });
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string, string, string)>();
        while (await r.ReadAsync())
        {
            rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        return rows;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
