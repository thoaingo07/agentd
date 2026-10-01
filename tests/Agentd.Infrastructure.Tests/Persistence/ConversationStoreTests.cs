using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class ConversationStoreTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private static readonly ProviderKey s_telegram = ProviderKey.From("telegram");
    private static NpgsqlDataSource s_db = null!;
    private static int s_nextWorkItem = 5000;

    [ClassInitialize]
    public static async Task InitAsync(TestContext _) => s_db = await Database.CreateMigratedAsync("conversations");

    [ClassCleanup]
    public static async Task CleanupAsync() => await s_db.DisposeAsync();

    [TestMethod]
    public async Task A_job_with_two_conversations_round_trips()
    {
        var job = await NewJobAsync();
        var store = new ConversationStore(s_db);
        var discord = Open(job, s_discord, "thread-" + job, new Uri("https://discord.com/channels/1/2"));
        var telegram = Open(job, s_telegram, "-100:" + job);
        Assert.IsTrue((await store.AddAsync(discord, default)).IsSuccess);
        Assert.IsTrue((await store.AddAsync(telegram, default)).IsSuccess);
        discord.SetStatusMessage("msg-1");
        await store.SaveAsync(discord, default);

        var loaded = await store.ListByJobAsync(job, default);

        Assert.HasCount(2, loaded);
        Assert.AreEqual("msg-1", loaded.Single(c => c.Provider == s_discord).StatusMessageId);
        Assert.AreEqual(new Uri("https://discord.com/channels/1/2"), loaded.Single(c => c.Provider == s_discord).Link);
        Assert.AreEqual(job, (await store.FindExternalAsync(s_telegram, "-100:" + job, default))?.JobId);
        Assert.IsNull(await store.FindExternalAsync(s_telegram, "nope", default));
    }

    [TestMethod]
    public async Task The_same_external_thread_cannot_be_used_twice()
    {
        var store = new ConversationStore(s_db);
        await store.AddAsync(Open(await NewJobAsync(), s_discord, "shared-thread"), default);

        var second = await store.AddAsync(Open(await NewJobAsync(), s_discord, "shared-thread"), default);

        Assert.AreEqual("conflict", second.Error?.Code);
    }

    [TestMethod]
    public async Task A_job_has_at_most_one_open_conversation_per_provider()
    {
        var job = await NewJobAsync();
        var store = new ConversationStore(s_db);
        var first = Open(job, s_discord, "a-" + job);
        await store.AddAsync(first, default);

        Assert.AreEqual("conflict", (await store.AddAsync(Open(job, s_discord, "b-" + job), default)).Error?.Code);

        first.Close(DateTimeOffset.UtcNow);
        await store.SaveAsync(first, default);
        Assert.IsTrue((await store.AddAsync(Open(job, s_discord, "c-" + job), default)).IsSuccess, "allowed once the first is closed");
    }

    [TestMethod]
    public async Task Opening_records_the_event_on_the_job()
    {
        var job = await NewJobAsync();
        await new ConversationStore(s_db).AddAsync(Open(job, s_discord, "evt-" + job), default);

        await using var cmd = s_db.CreateCommand("SELECT count(*) FROM agentd.events WHERE job_id = $1 AND type = 'ConversationOpened'");
        cmd.Parameters.Add(new NpgsqlParameter { Value = job.Value });
        Assert.AreEqual(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    // The aggregate check is bypassed on purpose (existing: []) so the database constraints are what's tested.
    private static Conversation Open(JobId job, ProviderKey provider, string external, Uri? link = null) =>
        Conversation.Open(job, provider, external, "space", link, [], DateTimeOffset.UtcNow).Value!;

    private static async Task<JobId> NewJobAsync()
    {
        var clock = new Clock();
        var job = Job.Create(WorkItemId.From(Interlocked.Increment(ref s_nextWorkItem)), RepositoryName.From("sysmin"), "t", clock);
        await new JobRepository(s_db, clock).AddAsync(job, default);
        return job.Id;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
