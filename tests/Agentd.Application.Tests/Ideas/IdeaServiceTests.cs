using System.Collections.Concurrent;
using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Application.Tests.Ideas;

[TestClass]
public sealed class IdeaServiceTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");

    [TestMethod]
    public async Task An_idea_opens_a_thread_and_the_agent_replies_there_on_its_model_and_effort()
    {
        var h = new Harness();
        h.Agent.Replies.Enqueue("Which pages need dark mode first?");

        var started = await h.Service.StartAsync(s_discord, "tngo", "dark mode for the portal", null, default, "opus", "high");
        await h.WaitForSentAsync(1);

        StringAssert.Contains(started.Value, "idea #1");
        StringAssert.Contains(started.Value, "Model: opus, effort: high");
        Assert.AreEqual("💡 Idea: dark mode for the portal", h.Chat.Opened.Single().Name);
        var turn = h.Agent.Turns.Single();
        Assert.AreEqual(("opus", "high", false), (turn.Model, turn.Effort, turn.Resume));
        StringAssert.Contains(turn.Prompt, "dark mode for the portal");
        Assert.AreEqual("idea-1", h.Worktrees.Detached.Single(), "a detached checkout per idea");
        Assert.AreEqual("Which pages need dark mode first?", h.Chat.SentText.Last());
        Assert.AreEqual(2, h.Store.Messages.Count, "the idea and the reply are kept");
    }

    [TestMethod]
    public async Task Every_message_resumes_the_same_session_and_proposals_are_shown()
    {
        var h = new Harness();
        h.Agent.Replies.Enqueue("Question?");
        await h.Service.StartAsync(s_discord, "tngo", "dark mode", null, default);
        await h.WaitForSentAsync(1);
        h.Agent.Replies.Enqueue("Plan.\n```work-items\n[{\"type\":\"User Story\",\"title\":\"Dark mode\"}]\n```");

        Assert.IsTrue(await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "all pages", default));
        await h.WaitForSentAsync(3);

        Assert.AreEqual(h.Agent.Turns[0].Session, h.Agent.Turns[1].Session);
        Assert.IsTrue(h.Agent.Turns[1].Resume);
        Assert.AreEqual("tngo: all pages", h.Agent.Turns[1].Prompt);
        Assert.AreEqual(IdeaStatus.Proposed, h.Store.Rows[1].Status);
        StringAssert.Contains(h.Chat.SentText.Last(), "Proposed work items");
    }

    [TestMethod]
    public async Task Settings_change_mid_thread_and_finished_ideas_refuse_messages()
    {
        var h = new Harness();
        h.Agent.Replies.Enqueue("ok");
        await h.Service.StartAsync(s_discord, "tngo", "dark mode", null, default);
        await h.WaitForSentAsync(1);

        StringAssert.Contains(await h.Service.ChangeSettingsAsync(h.Store.Rows[1], "sonnet", null, default), "model **sonnet**");
        StringAssert.Contains(await h.Service.ChangeSettingsAsync(h.Store.Rows[1], null, "turbo", default), "isn't an effort level");
        Assert.AreEqual("sonnet", h.Store.Rows[1].Model);

        h.Store.Rows[1] = h.Store.Rows[1] with { Status = IdeaStatus.Closed };
        Assert.IsFalse(await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "one more thing", default));
    }

    [TestMethod]
    public async Task With_several_repositories_the_repo_must_be_named()
    {
        var h = new Harness();
        h.Registry.Repositories.Add(h.Registry.Repositories[0] with { Name = Domain.Jobs.ValueObjects.RepositoryName.From("portal-mobile-app") });

        var started = await h.Service.StartAsync(s_discord, "tngo", "dark mode", null, default);

        StringAssert.Contains(started.Error!.Message, "--repo");
        Assert.IsEmpty(h.Chat.Opened);
    }

    [TestMethod]
    public async Task Create_and_start_makes_the_story_then_its_tasks_and_tags_the_story_for_agentd()
    {
        var h = new Harness();
        var idea = await h.ProposedAsync();

        Assert.IsTrue(await h.Service.HandleMessageAsync(idea, "tngo", "2", default));

        CollectionAssert.AreEqual(new[] { "User Story:Dark mode:", "Task:Tokens:9001", "Task:Toggle:9001" }, h.WorkItems.Created.Select(c => $"{c.Type}:{c.Title}:{c.ParentId}").ToArray());
        CollectionAssert.AreEqual(new[] { "ui", "ai-workflow", "repo:sysmin" }, h.WorkItems.Created[0].Tags.ToArray(), "agentd picks the story up");
        Assert.IsEmpty(h.WorkItems.Created[1].Tags, "tasks belong to the story's job");
        Assert.AreEqual("Portal\\Platform", h.WorkItems.Created[0].AreaPath, "the repository's area path");
        StringAssert.Contains(h.WorkItems.Created[0].Description, "From agentd idea #1");
        Assert.AreEqual(IdeaStatus.Created, h.Store.Rows[1].Status);
        CollectionAssert.AreEqual(new[] { 9001, 9002, 9003 }, h.Store.Rows[1].CreatedWorkItems.ToArray());
        StringAssert.Contains(h.Chat.SentText[^2], "User Story #9001:** Dark mode (https://dev.azure.com/ermsystem/Portal/_workitems/edit/9001)");
        StringAssert.Contains(h.Chat.SentText[^2], "Tagged `ai-workflow`");
        StringAssert.Contains(h.Chat.SentText[^1], "Delete this thread?");
    }

    [TestMethod]
    public async Task Create_without_start_adds_no_agentd_tags_and_change_asks_for_details()
    {
        var h = new Harness();
        var idea = await h.ProposedAsync();

        await h.Service.HandleMessageAsync(idea, "tngo", "3", default);
        Assert.IsEmpty(h.WorkItems.Created);
        StringAssert.Contains(h.Chat.SentText.Last(), "Tell me what to change");

        await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "✅ Create", default);
        Assert.IsFalse(h.WorkItems.Created.SelectMany(c => c.Tags).Contains("ai-workflow"));
    }

    [TestMethod]
    public async Task Discard_creates_nothing_then_the_close_out_deletes_the_thread()
    {
        var h = new Harness();
        var idea = await h.ProposedAsync();

        await h.Service.HandleMessageAsync(idea, "tngo", "discard", default);
        Assert.AreEqual(IdeaStatus.Discarded, h.Store.Rows[1].Status);
        StringAssert.Contains(h.Chat.SentText.Last(), "Delete this thread?");

        Assert.IsTrue(await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "1", default));

        Assert.AreEqual(IdeaStatus.Closed, h.Store.Rows[1].Status);
        CollectionAssert.Contains(h.Chat.DeletedThreads, idea.ThreadId);
        Assert.IsEmpty(h.WorkItems.Created);
    }

    [TestMethod]
    public async Task Close_ends_a_proposal_from_the_page_and_archives_its_thread_and_a_kept_thread_can_be_deleted_later()
    {
        var h = new Harness();
        var idea = await h.ProposedAsync();

        var closed = await h.Service.CloseAsync(idea, deleteThread: false, "tngo", default);

        Assert.IsTrue(closed.Value);
        Assert.AreEqual(IdeaStatus.Closed, h.Store.Rows[1].Status);
        Assert.IsEmpty(h.WorkItems.Created, "nothing created");
        Assert.IsTrue(h.Chat.SentText.Any(t => t.Contains("Closed by tngo; nothing was created.", StringComparison.Ordinal)));
        CollectionAssert.Contains(h.Chat.ArchivedThreads, idea.ThreadId);
        Assert.IsEmpty(h.Chat.DeletedThreads);

        var deleted = await h.Service.CloseAsync(h.Store.Rows[1], deleteThread: true, "tngo", default);
        var again = await h.Service.CloseAsync(h.Store.Rows[1], deleteThread: false, "tngo", default);

        Assert.IsTrue(deleted.Value);
        CollectionAssert.Contains(h.Chat.DeletedThreads, idea.ThreadId);
        Assert.AreEqual("conflict", again.Error?.Code, "already closed: only deleting its thread is left");
    }

    [TestMethod]
    public async Task Close_while_brainstorming_can_delete_the_thread_at_once()
    {
        var h = new Harness();
        var idea = await h.ProposedAsync();
        h.Store.Rows[1] = h.Store.Rows[1] with { Status = IdeaStatus.Brainstorming };

        var closed = await h.Service.CloseAsync(h.Store.Rows[1], deleteThread: true, "tngo", default);

        Assert.IsTrue(closed.Value);
        Assert.AreEqual(IdeaStatus.Closed, h.Store.Rows[1].Status);
        CollectionAssert.Contains(h.Chat.DeletedThreads, idea.ThreadId);
        Assert.IsEmpty(h.Chat.ArchivedThreads);
    }

    [TestMethod]
    public async Task Typing_close_in_the_thread_ends_it_at_any_stage_with_the_delete_or_keep_question()
    {
        var h = new Harness();
        var idea = await h.ProposedAsync();

        Assert.IsTrue(await h.Service.HandleMessageAsync(idea, "tngo", "close", default));

        Assert.AreEqual(IdeaStatus.Discarded, h.Store.Rows[1].Status);
        StringAssert.Contains(h.Chat.SentText.Last(), "Delete this thread?");
        Assert.IsTrue(await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "2", default));
        Assert.AreEqual(IdeaStatus.Closed, h.Store.Rows[1].Status);
        CollectionAssert.Contains(h.Chat.ArchivedThreads, idea.ThreadId);
    }

    [TestMethod]
    public async Task Agentds_messages_and_questions_reach_the_inbox_for_the_ideas_author()
    {
        var h = new Harness();
        var idea = await h.ProposedAsync();
        await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "discard", default);

        var entries = h.Inbox.Entries.ToList();
        Assert.IsTrue(entries.All(e => (e.Source, e.SourceId, e.Recipient) == ("idea", idea.Id, idea.Author)));
        Assert.IsTrue(entries.All(e => e.Title == $"💡 {idea.Title}"));
        Assert.IsTrue(entries.Any(e => e.Message.Markdown.Contains("Discarded by tngo", StringComparison.Ordinal)));
        var closeOut = entries.Last();
        Assert.AreEqual(MessageKind.Question, closeOut.Message.Kind, "the close-out question, though not kept in the idea's conversation");
        Assert.HasCount(2, closeOut.Message.Options!);
    }

    [TestMethod]
    public async Task While_proposed_other_text_goes_to_the_agent_to_revise()
    {
        var h = new Harness();
        var idea = await h.ProposedAsync();
        h.Agent.Replies.Enqueue("Revised.");

        await h.Service.HandleMessageAsync(idea, "tngo", "split the toggle into two tasks", default);
        await h.WaitForSentAsync(3);

        Assert.AreEqual("tngo: split the toggle into two tasks", h.Agent.Turns.Last().Prompt);
        Assert.IsEmpty(h.WorkItems.Created);
    }

    [TestMethod]
    public async Task A_web_idea_has_no_thread_its_page_gets_agentds_notices_and_it_needs_no_close_out()
    {
        var h = new Harness();
        h.Agent.Replies.Enqueue("Which pages first?");

        var id = (await h.Service.StartOnWebAsync("dev@example.com", "dark mode", null, "opus", "HIGH", default)).Value;
        await h.WaitForMessagesAsync(2);
        for (var i = 0; i < 300 && h.Service.IsThinking(id); i++)
        {
            await Task.Delay(10);
        }

        Assert.IsFalse(h.Service.IsThinking(id), "thinking ends with the reply");
        h.Agent.Replies.Enqueue("Plan.\n```work-items\n[{\"type\":\"User Story\",\"title\":\"Dark mode\"}]\n```");
        Assert.IsTrue(await h.Service.HandleMessageAsync(h.Store.Rows[id], "dev@example.com", "all pages", default, fromWeb: true));
        for (var i = 0; i < 300 && h.Store.Rows[id].Status != IdeaStatus.Proposed; i++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(await h.Service.HandleMessageAsync(h.Store.Rows[id], "dev@example.com", IdeaService.LabelDiscard, default, fromWeb: true));
        var late = await h.Service.HandleMessageAsync(h.Store.Rows[id], "dev@example.com", "one more thing", default, fromWeb: true);

        var idea = h.Store.Rows[id];
        Assert.AreEqual((IdeaService.Web, null, "opus", "high"), (idea.Provider, idea.ThreadId, idea.Model, idea.Effort));
        Assert.IsEmpty(h.Chat.Opened);
        Assert.IsEmpty(h.Chat.SentText, "nothing goes to a chat thread");
        Assert.AreEqual(IdeaStatus.Discarded, idea.Status, "no close-out question: there is no thread to delete");
        Assert.IsFalse(late);
        var notices = h.Store.Authored.Where(m => m.Author == "agentd").Select(m => m.Text).ToList();
        Assert.HasCount(1, notices, "agentd's own notice is kept for the page; the agent's replies are kept as the agent's");
        StringAssert.StartsWith(notices[0], "🗑 Discarded by dev@example.com");
        Assert.AreEqual(2, h.Store.Authored.Count(m => m.Author == "agent"));
        CollectionAssert.Contains(h.Worktrees.Removed, idea.Worktree, "its checkout is given back");
    }

    [TestMethod]
    public async Task A_message_from_the_web_on_a_chat_idea_is_posted_to_its_thread_too()
    {
        var h = new Harness();
        h.Agent.Replies.Enqueue("Question?");
        await h.Service.StartAsync(s_discord, "tngo", "dark mode", null, default);
        await h.WaitForSentAsync(1);

        await h.Service.HandleMessageAsync(h.Store.Rows[1], "dev@example.com", "all pages", default, fromWeb: true);
        await h.WaitForSentAsync(3);

        Assert.AreEqual("💬 **dev@example.com** (web): all pages", h.Chat.SentText[1]);
        Assert.AreEqual("dev@example.com: all pages", h.Agent.Turns.Last().Prompt);
    }

    [TestMethod]
    public async Task A_web_idea_needs_text_and_valid_settings()
    {
        var h = new Harness();

        var empty = await h.Service.StartOnWebAsync("dev@example.com", " ", null, null, null, default);
        var model = await h.Service.StartOnWebAsync("dev@example.com", "x", null, "gpt 4!", null, default);
        var effort = await h.Service.StartOnWebAsync("dev@example.com", "x", null, null, "huge", default);
        var repo = await h.Service.StartOnWebAsync("dev@example.com", "x", "nope", null, null, default);

        Assert.AreEqual(("validation", "validation", "validation", "not_found"), (empty.Error!.Code, model.Error!.Code, effort.Error!.Code, repo.Error!.Code));
        Assert.IsEmpty(h.Store.Rows);
    }

    [TestMethod]
    public async Task A_first_turn_that_failed_is_retried_with_the_idea_itself()
    {
        var h = new Harness();
        h.Worktrees.NextCheckoutFails = new InvalidOperationException("git fetch failed (128)");
        await h.Service.StartAsync(s_discord, "Kelvin", "a pipeline runner for datasync", null, default);
        await WaitUntil(() => !h.Service.IsThinking(1));
        h.Agent.Replies.Enqueue("Which pipelines?");

        await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "are you still working on it?", default);
        await h.WaitForSentAsync(1);

        var turn = h.Agent.Turns.Single();
        StringAssert.StartsWith(turn.Prompt, "Brainstorm this idea with the developer, Kelvin:\n\na pipeline runner for datasync");
        StringAssert.EndsWith(turn.Prompt, "tngo: are you still working on it?");
        Assert.IsFalse(turn.Resume, "a new session: the failed turn never started one");
        h.Agent.Replies.Enqueue("Got it.");
        await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "the full build", default);
        await h.WaitForSentAsync(2);
        Assert.AreEqual("tngo: the full build", h.Agent.Turns[1].Prompt, "once answered, only the new message");
    }

    [TestMethod]
    public async Task After_a_restart_ideas_the_agent_never_answered_are_picked_up_and_answered_ones_are_not()
    {
        var h = new Harness();
        h.Agent.Replies.Enqueue("Question?");
        await h.Service.StartAsync(s_discord, "tngo", "dark mode", null, default);
        await h.WaitForSentAsync(1);
        h.Worktrees.NextCheckoutFails = new InvalidOperationException("git fetch failed (128)");
        await h.Service.StartAsync(s_discord, "Kelvin", "a pipeline runner", null, default);
        await WaitUntil(() => !h.Service.IsThinking(2));
        await h.Store.AddMessageAsync(2, "out", "agentd", "💭 Still thinking about the last message", default);   // agentd's notice isn't an answer
        await h.Store.AddMessageAsync(2, "in", "tngo", "are you still working on it?", default);

        var restarted = new IdeaService(h.Store, h.Registry, h.Worktrees, h.Agent, h.Providers, NullLogger<IdeaService>.Instance, h.WorkItems);
        h.Agent.Replies.Enqueue("Which pipelines?");
        await restarted.ResumeAsync(default);
        await h.WaitForSentAsync(2);

        Assert.HasCount(2, h.Agent.Turns, "idea 1 was answered: left alone");
        StringAssert.StartsWith(h.Agent.Turns[1].Prompt, "Brainstorm this idea with the developer, Kelvin:\n\na pipeline runner");
        StringAssert.EndsWith(h.Agent.Turns[1].Prompt, "tngo: are you still working on it?");
        CollectionAssert.AreEqual(Array.Empty<long>(), (await h.Store.ListAwaitingReplyAsync(default)).ToArray(), "answered now");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition());
    }

    private sealed class Harness
    {
        public Harness()
        {
            var options = Microsoft.Extensions.Options.Options.Create(new MessagingOptions());
            options.Value.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
            Providers = new MessagingProviderRegistry([Chat], options);
            Service = new IdeaService(Store, Registry, Worktrees, Agent, Providers, NullLogger<IdeaService>.Instance, WorkItems, inbox: Inbox);
        }

        public MessagingProviderRegistry Providers { get; }

        public FakeChat Chat { get; } = new("discord");

        public FakeRegistry Registry { get; } = new();

        public FakeWorktrees Worktrees { get; } = new();

        public Agent Agent { get; } = new();

        public FakeWorkItems WorkItems { get; } = new();

        /// <summary>An idea whose agent proposed a story with two tasks.</summary>
        public async Task<Idea> ProposedAsync()
        {
            Agent.Replies.Enqueue("Plan.\n```work-items\n[{\"type\":\"User Story\",\"title\":\"Dark mode\",\"estimate\":5,\"tags\":[\"ui\"]}," +
                "{\"type\":\"Task\",\"title\":\"Tokens\",\"estimate\":4,\"parent\":0},{\"type\":\"Task\",\"title\":\"Toggle\",\"estimate\":3,\"parent\":0}]\n```");
            await Service.StartAsync(ProviderKey.From("discord"), "tngo", "dark mode", null, default);
            await WaitForSentAsync(2);
            return Store.Rows[1];
        }

        public Store Store { get; } = new();

        public IdeaService Service { get; }

        public FakeInbox Inbox { get; } = new();

        public async Task WaitForMessagesAsync(int count)
        {
            for (var i = 0; i < 300 && Store.Messages.Count < count; i++)
            {
                await Task.Delay(10);
            }

            Assert.IsGreaterThanOrEqualTo(count, Store.Messages.Count, "the reply was kept");
        }

        public async Task WaitForSentAsync(int count)
        {
            for (var i = 0; i < 300 && Chat.SentText.Count < count; i++)
            {
                await Task.Delay(10);
            }

            Assert.IsGreaterThanOrEqualTo(count, Chat.SentText.Count, "the reply was posted");
        }
    }

    private sealed class Agent : IBrainstormAgent
    {
        public ConcurrentQueue<string> Replies { get; } = new();

        public List<BrainstormTurn> Turns { get; } = [];

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            Turns.Add(turn);
            return Task.FromResult(new BrainstormReply(Replies.TryDequeue(out var r) ? r : "…", null, null));
        }
    }

    private sealed class Store : IIdeaStore
    {
        public ConcurrentDictionary<long, Idea> Rows { get; } = new();

        public List<(long Idea, string Direction, string Text)> Messages { get; } = [];

        public List<(string Direction, string Author, string Text)> Authored { get; } = [];

        public Task<long> InsertAsync(string repository, string title, string author, ProviderKey provider, string? threadId, string? spaceId, CancellationToken cancellationToken)
        {
            var id = Rows.Count + 1;
            Rows[id] = new Idea(id, repository, title, author, provider, threadId, spaceId, IdeaStatus.Brainstorming, null, null, null, null, null, []);
            return Task.FromResult((long)id);
        }

        public Task<Idea?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(Rows.TryGetValue(id, out var i) ? i : null);

        public Task<Idea?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.Values.FirstOrDefault(i => i.ThreadId == threadId));

        public Task SaveAsync(Idea idea, CancellationToken cancellationToken)
        {
            Rows[idea.Id] = idea;
            return Task.CompletedTask;
        }

        public Task AddMessageAsync(long ideaId, string direction, string author, string text, CancellationToken cancellationToken)
        {
            lock (Messages)
            {
                Messages.Add((ideaId, direction, text));
                Authored.Add((direction, author, text));
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long ideaId, CancellationToken cancellationToken)
        {
            lock (Messages)
            {
                return Task.FromResult<IReadOnlyList<IdeaMessage>>([.. Messages.Select((m, i) => (m, i)).Where(x => x.m.Idea == ideaId)
                    .Select(x => new IdeaMessage(x.m.Direction, Authored[x.i].Author, x.m.Text, DateTimeOffset.UnixEpoch))]);
            }
        }

        public async Task<IReadOnlyList<long>> ListAwaitingReplyAsync(CancellationToken cancellationToken)
        {
            var waiting = new List<long>();
            foreach (var idea in Rows.Values.Where(i => i.Status is IdeaStatus.Brainstorming or IdeaStatus.Proposed).OrderBy(i => i.Id))
            {
                var messages = await ListMessagesAsync(idea.Id, cancellationToken);
                var lastReply = messages.Select((m, i) => (m, i)).LastOrDefault(x => x.m is { Direction: "out", Author: "agent" }, (default!, -1)).i;
                if (messages.Skip(lastReply + 1).Any(m => m.Direction == "in"))
                {
                    waiting.Add(idea.Id);
                }
            }

            return waiting;
        }

        public Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Rows.Values.Where(i => i.Status != IdeaStatus.Closed && i.ThreadId is not null).Select(i => i.ThreadId!).ToList());

        public Task<IReadOnlyList<IdeaSummary>> ListSummariesAsync(long? id, int? workItem, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IdeaSummary>>(Rows.Values.Where(i => (id is null || i.Id == id) && (workItem is null || i.CreatedWorkItems.Contains(workItem.Value)))
                .OrderByDescending(i => i.Id).Take(limit)
                .Select(i => new IdeaSummary(i.Id, i.Repository, i.Title, i.Author, i.Status, i.Model, i.Effort, i.Drafts?.Count ?? 0, i.CreatedWorkItems, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                .ToList());
    }
}
