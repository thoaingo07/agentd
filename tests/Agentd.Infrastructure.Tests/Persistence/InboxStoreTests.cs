using Agentd.Application.Inbox;
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
public sealed class InboxStoreTests
{
    private static NpgsqlDataSource s_db = null!;
    private static int s_nextWorkItem = 8100;
    private static long s_nextSource = 1000;
    private readonly Clock _clock = new();

    [ClassInitialize]
    public static async Task InitAsync(TestContext _) => s_db = await Database.CreateMigratedAsync("inbox");

    [ClassCleanup]
    public static async Task CleanupAsync() => await s_db.DisposeAsync();

    [TestMethod]
    public async Task Every_job_message_but_progress_lands_in_the_inbox_even_without_a_chat_thread()
    {
        var job = await RunningJobAsync();   // no conversation: chat is off
        var outbox = new Outbox(s_db);
        await outbox.EnqueueAsync(job.Id, [new OutboxMessage(MessageCatalog.Progress("step 1"), new EnqueueOptions(ReplaceStatusMessage: true))], default);
        await outbox.EnqueueAsync(job.Id, [new OutboxMessage(new OutboundMessage(MessageKind.Info, "📝 Plan: page by key."))], default);

        job.AskDeveloper("Which table first?", ["orders", "items"]);
        Assert.IsTrue((await new JobRepository(s_db, _clock).SaveAsync(job, default)).IsSuccess);

        var store = new InboxStore(s_db);
        var items = await store.ThreadAsync(InboxSources.Job, job.Id.Value, null, 50, default);
        Assert.AreEqual("Question", items[0].Kind, "newest first");
        StringAssert.Contains(items[0].Markdown, "Which table first?");
        Assert.IsTrue(items.Any(i => i.Markdown == "📝 Plan: page by key."));
        Assert.IsFalse(items.Any(i => i.Kind == "Progress"), "progress updates aren't kept");
        Assert.AreEqual($"WI-{job.WorkItemId.Value} t", items[0].Title);

        var thread = (await store.ThreadsAsync("tngo", null, 500, default)).Single(t => t.Source == InboxSources.Job && t.SourceId == job.Id.Value);
        Assert.IsTrue(thread.Waiting, "the job waits for a person");
        Assert.AreEqual(items[0].Id, thread.Question!.Id);
        Assert.AreEqual(items.Count, thread.Unread);
    }

    [TestMethod]
    public async Task Threads_come_waiting_first_then_unread_then_read_and_mark_read_moves_a_waiting_one_down()
    {
        var store = new InboxStore(s_db);
        var ideas = new IdeaStore(s_db);
        var waitingIdea = await ideas.InsertAsync("sysmin", "Dark mode", "tngo", ProviderKey.From("web"), null, null, default);
        await ideas.SaveAsync((await ideas.GetAsync(waitingIdea, default))! with { Status = "Proposed" }, default);
        var w = await store.AddAsync(Entry(InboxSources.Idea, waitingIdea, "💡 Dark mode", "Create these?", "tngo",
            [new MessageOption("idea-create", "✅ Create"), new MessageOption("idea-discard", "🗑 Discard")], MessageKind.Question), default);
        var unread = Interlocked.Increment(ref s_nextSource);
        var u = await store.AddAsync(Entry(InboxSources.Chat, unread, "💬 Chat", "An answer.", "kelvin"), default);
        var read = Interlocked.Increment(ref s_nextSource);
        var r = await store.AddAsync(Entry(InboxSources.Chat, read, "💬 Chat", "Older answer.", "tngo"), default);
        await store.MarkReadAsync("tngo", InboxSources.Chat, read, r, default);

        var mine = Ours(await store.ThreadsAsync("tngo", null, 500, default), waitingIdea, unread, read);
        CollectionAssert.AreEqual(new[] { w, u, r }, mine.Select(t => t.LatestId).ToArray());
        Assert.AreEqual(("Create these?", 2), (mine[0].Question!.Markdown, mine[0].Question!.Options.Count));
        Assert.AreEqual("✅ Create", mine[0].Question!.Options[0].Label);
        Assert.AreEqual(0, mine[2].Unread);

        await store.MarkReadAsync("tngo", InboxSources.Idea, waitingIdea, w, default);
        mine = Ours(await store.ThreadsAsync("tngo", null, 500, default), waitingIdea, unread, read);
        CollectionAssert.AreEqual(new[] { u, r, w }, mine.Select(t => t.LatestId).ToArray(), "read: out of the top, among the rest by newest, until agentd writes again");
        Assert.IsTrue(mine[2].Waiting);

        var kelvins = await store.ThreadsAsync("kelvin", "kelvin", 500, default);
        CollectionAssert.AreEqual(new[] { unread }, kelvins.Where(t => t.SourceId == unread || t.SourceId == read || t.SourceId == waitingIdea).Select(t => t.SourceId).ToArray(),
            "Mine: only threads addressed to them");
    }

    [TestMethod]
    public async Task Counts_say_how_many_threads_wait_and_how_many_have_unread_messages()
    {
        await using var db = await Database.CreateMigratedAsync("inbox_counts");
        var store = new InboxStore(db);
        var ideas = new IdeaStore(db);
        var idea = await ideas.InsertAsync("sysmin", "Dark mode", "tngo", ProviderKey.From("web"), null, null, default);
        await ideas.SaveAsync((await ideas.GetAsync(idea, default))! with { Status = "Proposed" }, default);
        await store.AddAsync(Entry(InboxSources.Idea, idea, "💡", "Create these?", "tngo", null, MessageKind.Question), default);
        var last = await store.AddAsync(Entry(InboxSources.Chat, 7, "💬", "Hi.", "tngo"), default);

        Assert.AreEqual(new InboxCounts(1, 2), await store.CountsAsync("tngo", default));
        await store.MarkReadAsync("tngo", InboxSources.Chat, 7, last, default);
        Assert.AreEqual(new InboxCounts(1, 1), await store.CountsAsync("tngo", default));
        Assert.AreEqual(new InboxCounts(1, 2), await store.CountsAsync("kelvin", default), "read marks are per person");
    }

    [TestMethod]
    public async Task Mark_read_never_moves_back_even_from_parallel_callers()
    {
        var store = new InboxStore(s_db);
        var source = Interlocked.Increment(ref s_nextSource);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await store.AddAsync(Entry(InboxSources.Chat, source, "💬", $"m{i}", "tngo"), default));
        }

        await Task.WhenAll(ids.Concat(ids).Select(id => store.MarkReadAsync("tngo", InboxSources.Chat, source, id, default)));
        await store.MarkReadAsync("tngo", InboxSources.Chat, source, ids[0], default);

        var thread = (await store.ThreadsAsync("tngo", null, 500, default)).Single(t => t.Source == InboxSources.Chat && t.SourceId == source);
        Assert.AreEqual((0, 5), (thread.Unread, thread.Items));
    }

    [TestMethod]
    public async Task Parallel_writers_each_get_their_message_and_an_event()
    {
        var store = new InboxStore(s_db);
        var source = Interlocked.Increment(ref s_nextSource);

        var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.AddAsync(Entry(InboxSources.Watch, source, "👀", $"n{i}", "tngo"), default)));

        Assert.HasCount(20, ids.Distinct());
        Assert.HasCount(20, await store.ThreadAsync(InboxSources.Watch, source, null, 100, default));
        await using var events = s_db.CreateCommand("SELECT count(*) FROM agentd.events WHERE type = 'inbox.added' AND (payload ->> 'sourceId')::bigint = $1 AND payload::text NOT LIKE '%n1%'");
        events.Parameters.Add(new NpgsqlParameter { Value = source });
        Assert.AreEqual(20L, (long)(await events.ExecuteScalarAsync())!, "one event each, ids only (never the text)");
    }

    [TestMethod]
    public async Task Retention_drops_old_messages_and_the_read_marks_left_without_any()
    {
        await using var db = await Database.CreateMigratedAsync("inbox_retention");
        var store = new InboxStore(db);
        var old = await store.AddAsync(Entry(InboxSources.Chat, 1, "💬", "old", "tngo"), default);
        await store.MarkReadAsync("tngo", InboxSources.Chat, 1, old, default);
        await using (var age = db.CreateCommand("UPDATE agentd.inbox_items SET created_at = now() - interval '100 days'"))
        {
            await age.ExecuteNonQueryAsync();
        }

        await store.AddAsync(Entry(InboxSources.Chat, 2, "💬", "new", "tngo"), default);

        Assert.AreEqual(1, await store.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddDays(-90), default));
        Assert.AreEqual(2, (await store.ThreadsAsync("tngo", null, 500, default)).Single().SourceId);
        await using var reads = db.CreateCommand("SELECT count(*) FROM agentd.inbox_reads");
        Assert.AreEqual(0L, (long)(await reads.ExecuteScalarAsync())!);
    }

    private static List<InboxThread> Ours(IReadOnlyList<InboxThread> threads, params long[] sources) =>
        [.. threads.Where(t => sources.Contains(t.SourceId) && t.Source != InboxSources.Job)];

    private static InboxEntry Entry(string source, long id, string title, string text, string? to, IReadOnlyList<MessageOption>? options = null,
        MessageKind kind = MessageKind.Info) =>
        new(source, id, title, to, new OutboundMessage(kind, text, options));

    private async Task<Job> RunningJobAsync()
    {
        var repo = new JobRepository(s_db, _clock);
        var job = Job.Create(WorkItemId.From(Interlocked.Increment(ref s_nextWorkItem)), RepositoryName.From("sysmin"), "t", _clock);
        await repo.AddAsync(job, default);
        job.BeginPreparing();
        job.Start(new WorktreePath("/wt"), BranchName.From($"ai/{job.WorkItemId}-t"), ClaudeSessionId.New());
        await repo.SaveAsync(job, default);
        return job;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
