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

    private sealed class Harness
    {
        public Harness()
        {
            var options = Microsoft.Extensions.Options.Options.Create(new MessagingOptions());
            options.Value.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
            Service = new IdeaService(Store, Registry, Worktrees, Agent, new MessagingProviderRegistry([Chat], options), NullLogger<IdeaService>.Instance, WorkItems);
        }

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

        public Task<long> InsertAsync(string repository, string title, string author, ProviderKey provider, string threadId, string? spaceId, CancellationToken cancellationToken)
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
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long ideaId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IdeaMessage>>([]);

        public Task<IReadOnlyList<IdeaSummary>> ListSummariesAsync(long? id, int? workItem, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IdeaSummary>>(Rows.Values.Where(i => (id is null || i.Id == id) && (workItem is null || i.CreatedWorkItems.Contains(workItem.Value)))
                .OrderByDescending(i => i.Id).Take(limit)
                .Select(i => new IdeaSummary(i.Id, i.Repository, i.Title, i.Author, i.Status, i.Model, i.Effort, i.Drafts?.Count ?? 0, i.CreatedWorkItems, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                .ToList());
    }
}
