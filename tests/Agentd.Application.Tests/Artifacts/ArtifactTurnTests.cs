using Agentd.Application.Artifacts;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Tests.Artifacts;

[TestClass]
public sealed class ArtifactTurnTests
{
    private readonly Tokens _tokens = new();
    private readonly Agent _agent = new();

    [TestMethod]
    public async Task A_turn_gets_its_own_artifact_token_which_is_revoked_after_even_when_it_fails()
    {
        var turn = new BrainstormTurn(5, "/wt", Guid.NewGuid(), false, "draft a page");

        await ArtifactTurn.RunAsync(_agent, _tokens, ArtifactSources.Idea, 5, turn, default);
        _agent.Fail = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ArtifactTurn.RunAsync(_agent, _tokens, ArtifactSources.Review, 9, turn, default));

        CollectionAssert.AreEqual(new[] { "idea:5:1", "review:9:2" }, _agent.Tokens);
        CollectionAssert.AreEqual(new[] { "idea:5:1", "review:9:2" }, _tokens.Revoked, "revoked after the turn, also after a failure");
    }

    [TestMethod]
    public async Task A_chats_own_token_is_kept_and_without_an_issuer_nothing_changes()
    {
        await ArtifactTurn.RunAsync(_agent, _tokens, ArtifactSources.Chat, 7, new BrainstormTurn(7, "/wt", Guid.NewGuid(), false, "q", McpToken: "chat-token"), default);
        await ArtifactTurn.RunAsync(_agent, null, ArtifactSources.Idea, 5, new BrainstormTurn(5, "/wt", Guid.NewGuid(), false, "q"), default);

        CollectionAssert.AreEqual(new string?[] { "chat-token", null }, _agent.Tokens);
        Assert.IsEmpty(_tokens.Revoked, "a chat revokes its token when it closes");
    }

    [TestMethod]
    public async Task An_ideas_turn_runs_with_an_artifact_token()
    {
        var t = new Fakes.TestContext();
        var options = Microsoft.Extensions.Options.Options.Create(new Agentd.Application.Messaging.MessagingOptions());
        using var service = new IdeaService(new Ideas(), t.Registry, t.Worktrees, _agent, new Agentd.Application.Messaging.MessagingProviderRegistry([], options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IdeaService>.Instance, tokens: _tokens);

        await service.StartOnWebAsync("dev@example.com", "dark mode", null, null, null, default);
        await _agent.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100 && _tokens.Revoked.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.AreEqual("idea:1:1", _agent.Tokens.Single());
        CollectionAssert.AreEqual(new[] { "idea:1:1" }, _tokens.Revoked);
    }

    private sealed class Ideas : IIdeaStore
    {
        private Idea? _idea;

        public Task<long> InsertAsync(string repository, string title, string author, Domain.Messaging.ProviderKey provider, string? threadId, string? spaceId, CancellationToken cancellationToken)
        {
            _idea = new Idea(1, repository, title, author, provider, threadId, spaceId, IdeaStatus.Brainstorming, null, null, null, null, null, []);
            return Task.FromResult(1L);
        }

        public Task<Idea?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(_idea);

        public Task<Idea?> FindByThreadAsync(Domain.Messaging.ProviderKey provider, string threadId, CancellationToken cancellationToken) => Task.FromResult<Idea?>(null);

        public Task SaveAsync(Idea idea, CancellationToken cancellationToken)
        {
            _idea = idea;
            return Task.CompletedTask;
        }

        public Task AddMessageAsync(long ideaId, string direction, string author, string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<IdeaMessage>> ListMessagesAsync(long ideaId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IdeaMessage>>([]);

        public Task<IReadOnlyList<IdeaSummary>> ListSummariesAsync(long? id, int? workItem, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IdeaSummary>>([]);

        public Task<IReadOnlyList<string>> ListOpenThreadsAsync(Domain.Messaging.ProviderKey provider, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<long>> ListAwaitingReplyAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>([]);
    }

    private sealed class Agent : IBrainstormAgent
    {
        public List<string?> Tokens { get; } = [];

        public bool Fail { get; set; }

        public TaskCompletionSource Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            Tokens.Add(turn.McpToken);
            Ran.TrySetResult();
            return Fail ? throw new InvalidOperationException("the CLI died") : Task.FromResult(new BrainstormReply("ok", null, null));
        }
    }

    private sealed class Tokens : IMcpTokenIssuer
    {
        private int _next;

        public List<string> Revoked { get; } = [];

        public string? IssueArtifacts(string source, long id) => $"{source}:{id}:{++_next}";

        public void RevokeToken(string token) => Revoked.Add(token);

        public string Issue(JobId jobId) => throw new NotSupportedException();

        public JobId? Validate(string token) => null;

        public void Revoke(JobId jobId)
        {
        }

        public string IssueChat(long chatId) => throw new NotSupportedException();

        public long? ValidateChat(string token) => null;

        public void RevokeChat(long chatId)
        {
        }
    }
}
