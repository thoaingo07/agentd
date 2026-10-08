using System.Collections.Concurrent;
using Agentd.Application.Chats;
using Agentd.Application.Ideas;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Application.Tests.Chats;

[TestClass]
public sealed class ChatServiceTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");

    [TestMethod]
    public async Task A_question_opens_a_thread_and_reads_every_repository()
    {
        var h = new Harness();
        h.Agent.Replies.Enqueue("Login tokens are validated in `src/Auth/TokenValidator.cs:42`.");

        var started = await h.Service.StartAsync(s_discord, "tngo", ["where", "do", "we", "validate", "the", "login", "token?"], default);
        await h.WaitForSentAsync(1);

        StringAssert.Contains(started.Value, "chat #1");
        Assert.AreEqual("💬 Chat: where do we validate the login token?", h.Chat.Opened.Single().Name);
        StringAssert.Contains(h.Chat.Opened.Single().Opening.Markdown, "`sysmin`, `portal`");
        var turn = h.Agent.Turns.Single();
        Assert.AreEqual((ThreadTurnKind.Chat, false, "/home/agentd/.agentd/worktrees/sysmin/chat-1"), (turn.Kind, turn.Resume, turn.Worktree));
        CollectionAssert.AreEqual(new[] { "/home/agentd/.agentd/worktrees/portal/chat-1" }, turn.AddDirs!.ToList(), "the other repositories are added");
        StringAssert.Contains(turn.Prompt, "- portal (main): /home/agentd/.agentd/worktrees/portal/chat-1");
        StringAssert.Contains(turn.Prompt, "tngo asks:\n\nwhere do we validate the login token?");
        Assert.AreEqual(("claude-opus-5-5", "high"), (turn.Model, turn.Effort), "the chat step's defaults");
        StringAssert.Contains(h.Chat.SentText[0], "TokenValidator.cs:42");
    }

    [TestMethod]
    public async Task Follow_ups_resume_the_session_and_close_ends_it()
    {
        var h = new Harness();
        await h.Service.StartAsync(s_discord, "tngo", ["what", "is", "sysmin?"], default);
        await h.WaitForSentAsync(1);

        Assert.IsTrue(await h.Service.HandleMessageAsync(h.Store.Rows[1], "kelvin", "and who calls it?", default));
        await h.WaitForSentAsync(2);
        Assert.AreEqual(("kelvin: and who calls it?", true), (h.Agent.Turns[1].Prompt, h.Agent.Turns[1].Resume));
        Assert.AreEqual(h.Agent.Turns[0].Session, h.Agent.Turns[1].Session);

        Assert.AreEqual("chat-token-1", h.Agent.Turns[0].McpToken, "the chat's own token for agentd's Azure DevOps tools");
        Assert.IsTrue(await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "close", default));
        CollectionAssert.AreEqual(new[] { 1L }, h.Tokens.Revoked, "closing revokes it");
        Assert.AreEqual(ChatStatus.Closed, h.Store.Rows[1].Status);
        Assert.HasCount(2, h.Worktrees.Removed, "both checkouts are removed");
        StringAssert.Contains(h.Chat.SentText.Last(), "Chat closed by tngo");
        Assert.IsFalse(await h.Service.HandleMessageAsync(h.Store.Rows[1], "tngo", "one more", default));
    }

    [TestMethod]
    public async Task Repo_narrows_the_chat_and_bad_input_is_refused()
    {
        var h = new Harness();

        await h.Service.StartAsync(s_discord, "tngo", ["--repo", "portal", "what", "does", "it", "do?"], default);
        var unknown = await h.Service.StartAsync(s_discord, "tngo", ["--repo", "nope", "hi"], default);
        var empty = await h.Service.StartAsync(s_discord, "tngo", ["--model", "opus"], default);
        await h.WaitForSentAsync(1);

        CollectionAssert.AreEqual(new[] { "portal" }, h.Store.Rows[1].Repositories.ToList());
        Assert.IsEmpty(h.Agent.Turns.Single().AddDirs ?? [], "one repository: nothing added");
        StringAssert.Contains(unknown.Error!.Message, "No repository `nope`");
        StringAssert.Contains(empty.Error!.Message, "Ask something");
    }

    private sealed class Harness
    {
        public Harness()
        {
            var options = Microsoft.Extensions.Options.Options.Create(new MessagingOptions());
            options.Value.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
            Registry.Repositories.Add(new Repository(RepositoryName.From("portal"), "git@erm-azdo:v3/ermsystem/Portal/portal", new AzureDevOpsRepo("ermsystem", "Portal", "portal"), "main", "repo:portal", []));
            var jobs = new JobOptions();
            jobs.Steps[JobSteps.Chat] = new StepModel { Model = "claude-opus-5-5", Effort = "High" };
            Service = new ChatService(Store, Registry, Worktrees, Agent, new MessagingProviderRegistry([Chat], options), NullLogger<ChatService>.Instance, Microsoft.Extensions.Options.Options.Create(jobs), Tokens);
        }

        public FakeChat Chat { get; } = new("discord");

        public FakeRegistry Registry { get; } = new();

        public FakeWorktrees Worktrees { get; } = new();

        public Agent Agent { get; } = new();

        public Store Store { get; } = new();

        public ChatService Service { get; }

        public Tokens Tokens { get; } = new();

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

    private sealed class Tokens : Ports.IMcpTokenIssuer
    {
        public List<long> Revoked { get; } = [];

        public string Issue(JobId jobId) => throw new NotSupportedException();

        public JobId? Validate(string token) => null;

        public void Revoke(JobId jobId)
        {
        }

        public string IssueChat(long chatId) => $"chat-token-{chatId}";

        public long? ValidateChat(string token) => null;

        public void RevokeChat(long chatId) => Revoked.Add(chatId);
    }

    private sealed class Store : IChatStore
    {
        public ConcurrentDictionary<long, Chat> Rows { get; } = new();

        public Task<long> InsertAsync(string author, ProviderKey provider, string threadId, string? spaceId, IReadOnlyList<string> repositories, CancellationToken cancellationToken)
        {
            var id = Rows.Count + 1;
            Rows[id] = new Chat(id, author, provider, threadId, spaceId, ChatStatus.Open, repositories, null, null, null, []);
            return Task.FromResult((long)id);
        }

        public Task<Chat?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(Rows.TryGetValue(id, out var c) ? c : null);

        public Task<Chat?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.Values.FirstOrDefault(c => c.ThreadId == threadId));

        public Task SaveAsync(Chat chat, CancellationToken cancellationToken)
        {
            Rows[chat.Id] = chat;
            return Task.CompletedTask;
        }

        public Task AddMessageAsync(long chatId, string direction, string author, string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. Rows.Values.Where(c => c.Status == ChatStatus.Open).Select(c => c.ThreadId)]);
    }
}
