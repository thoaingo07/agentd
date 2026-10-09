using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Agentd.Application.Events;
using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class ReviewSessionEndpointTests
{
    private static readonly Repository s_sysmin = new(RepositoryName.From("sysmin"), "git@x:v3/o/p/sysmin", new AzureDevOpsRepo("o", "p", "sysmin"), "develop", "repo:sysmin", []);
    private readonly Sessions _store = new();

    [TestMethod]
    public async Task A_branch_review_starts_pinned_then_takes_decisions_and_comments()
    {
        await using var app = await StartAsync();
        var client = await new AntiforgeryClient(app).InitAsync();

        using var start = await client.PostAsync("/api/reviews", new { repo = "sysmin", branch = "feature/keyset" });
        Assert.AreEqual(HttpStatusCode.Created, start.StatusCode, await start.Content.ReadAsStringAsync());
        var session = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement;
        var id = session.GetProperty("id").GetInt64();
        Assert.AreEqual(("branch", "m1", "h1", "Ready", "local"), (session.GetProperty("target").GetString(), session.GetProperty("baseCommit").GetString(),
            session.GetProperty("headCommit").GetString(), session.GetProperty("status").GetString(), session.GetProperty("createdBy").GetString()));

        var http = app.GetTestClient();
        var diff = JsonDocument.Parse(await http.GetStringAsync(new Uri($"/api/reviews/{id}/diff", UriKind.Relative))).RootElement;
        Assert.AreEqual("src/Sync.cs", diff.GetProperty("files")[0].GetString());

        await _store.AddFindingsAsync(id, [new SessionFinding("breaks", "src/Sync.cs", 41, "Unbounded read", "why", "fix")], "One bug.", default);
        using var decide = await client.SendAsync(HttpMethod.Put, $"/api/reviews/{id}/findings/1", new { decision = "edited", text = "Page by key." });
        using var noSuchFinding = await client.SendAsync(HttpMethod.Put, $"/api/reviews/{id}/findings/9", new { decision = "dropped" });
        using var comment = await client.PostAsync($"/api/reviews/{id}/comments", new { file = "src/Sync.cs", line = 58, text = "make it config" });
        var commentId = JsonDocument.Parse(await comment.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        var detail = JsonDocument.Parse(await http.GetStringAsync(new Uri($"/api/reviews/{id}", UriKind.Relative))).RootElement;
        var finding = detail.GetProperty("session").GetProperty("findings")[0];
        Assert.AreEqual((HttpStatusCode.NoContent, HttpStatusCode.NotFound, HttpStatusCode.Created), (decide.StatusCode, noSuchFinding.StatusCode, comment.StatusCode));
        Assert.AreEqual((1, "edited", "Page by key."), (finding.GetProperty("number").GetInt32(), finding.GetProperty("decision").GetString(), finding.GetProperty("edited").GetString()));
        Assert.AreEqual("make it config", detail.GetProperty("comments")[0].GetProperty("text").GetString());

        using var delete = await client.SendAsync(HttpMethod.Delete, $"/api/reviews/{id}/comments/{commentId}");
        var mine = JsonDocument.Parse(await http.GetStringAsync(new Uri("/api/reviews", UriKind.Relative))).RootElement;
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.AreEqual(id, mine[0].GetProperty("id").GetInt64());
    }

    [TestMethod]
    public async Task Unknown_things_are_404_and_changes_need_the_antiforgery_token()
    {
        await using var app = await StartAsync();
        var client = await new AntiforgeryClient(app).InitAsync();

        using var noRepo = await client.PostAsync("/api/reviews", new { repo = "nope", branch = "x" });
        using var noBranch = await client.PostAsync("/api/reviews", new { repo = "sysmin", branch = "not-pushed" });
        using var nothing = await client.PostAsync("/api/reviews", new { repo = "sysmin" });
        using var noSession = await app.GetTestClient().GetAsync(new Uri("/api/reviews/999", UriKind.Relative));
        using var forged = await app.GetTestClient().PostAsJsonAsync(new Uri("/api/reviews", UriKind.Relative), new { repo = "sysmin", branch = "feature/keyset" });

        Assert.AreEqual((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.BadRequest),
            (noRepo.StatusCode, noBranch.StatusCode, nothing.StatusCode, noSession.StatusCode, forged.StatusCode));
        Assert.IsEmpty(_store.All);
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<ILiveEvents>(new EventHub(NullLogger<EventHub>.Instance));
        builder.Services.AddSingleton<IReviewSessionStore>(_store);
        builder.Services.AddSingleton(Stub<IRepositoryRegistry>.Create((name, args) =>
            name == nameof(IRepositoryRegistry.GetAsync) ? Task.FromResult(((RepositoryName)args[0]!).Value == "sysmin" ? s_sysmin : null) : null));
        builder.Services.AddSingleton(Stub<IWorktreeManager>.Create((name, args) => name switch
        {
            nameof(IWorktreeManager.EnsureCloneAsync) => Task.CompletedTask,
            nameof(IWorktreeManager.ResolveCommitAsync) => Task.FromResult((string)args[1]! switch { "feature/keyset" => "h1", "develop" => "d9", _ => null }),
            nameof(IWorktreeManager.MergeBaseAsync) => Task.FromResult<string?>("m1"),
            nameof(IWorktreeManager.DiffCommitsAsync) => Task.FromResult(new BranchDiff("m1", "h1", ["src/Sync.cs"], "diff --git a/src/Sync.cs b/src/Sync.cs\n", false)),
            _ => throw new NotSupportedException(name),
        }));
        builder.Services.AddSingleton(Stub<IPullRequestService>.Create((name, _) => throw new NotSupportedException(name)));
        builder.Services.AddSingleton<ReviewSessionService>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    /// <summary>An interface answered by one function (method name, arguments).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy derives from it at runtime.")]
    internal class Stub<T> : DispatchProxy
        where T : class
    {
        private Func<string, object?[], object?> _handle = (_, _) => null;

        public static T Create(Func<string, object?[], object?> handle)
        {
            var proxy = Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy)._handle = handle;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handle(targetMethod!.Name, args ?? []);
    }

    /// <summary>Sessions in memory: enough of the routines' rules for the endpoints.</summary>
    private sealed class Sessions : IReviewSessionStore
    {
        private readonly List<ReviewComment> _comments = [];
        private long _next;

        public Dictionary<long, ReviewSession> All { get; } = [];

        public Task<long> InsertAsync(string repository, string target, int? pullRequestId, string? headRef, string? baseRef, string createdBy, string? model, string? effort, CancellationToken cancellationToken)
        {
            var id = ++_next;
            All[id] = new(id, repository, target, pullRequestId, headRef, baseRef, null, null, ReviewSessionStatus.Reviewing, null, model, effort, null, [], null, null, createdBy, null, default, default);
            return Task.FromResult(id);
        }

        public Task<ReviewSession?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(All.GetValueOrDefault(id));

        public Task<IReadOnlyList<ReviewSession>> ListAsync(string createdBy, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReviewSession>>([.. All.Values.Where(s => s.CreatedBy == createdBy).OrderByDescending(s => s.Id)]);

        public Task<IReadOnlyList<ReviewSession>> ListByStatusAsync(string status, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReviewSession>>([.. All.Values.Where(s => s.Status == status).OrderBy(s => s.Id)]);

        public Task PinAsync(long id, string baseCommit, string headCommit, string? worktree, CancellationToken cancellationToken) => Update(id, s => s with { BaseCommit = baseCommit, HeadCommit = headCommit });

        public Task SetStatusAsync(long id, string status, string? reason, string? sentTo, CancellationToken cancellationToken) => Update(id, s => s with { Status = status });

        public async Task<int> AddFindingsAsync(long id, IReadOnlyList<SessionFinding> findings, string? summary, CancellationToken cancellationToken)
        {
            await Update(id, s => s with { Findings = [.. s.Findings, .. findings] });
            return All[id].Findings.Count;
        }

        public Task<bool> DecideAsync(long id, int index, string decision, string? edited, CancellationToken cancellationToken)
        {
            if (index < 0 || index >= All[id].Findings.Count)
            {
                return Task.FromResult(false);
            }

            var findings = All[id].Findings.ToList();
            findings[index] = findings[index] with { Decision = decision, Edited = edited };
            All[id] = All[id] with { Findings = findings };
            return Task.FromResult(true);
        }

        public Task<long> AddCommentAsync(long sessionId, string? file, int? line, int? endLine, string text, string author, CancellationToken cancellationToken)
        {
            _comments.Add(new ReviewComment(++_next, file, line, endLine, text, author, default));
            return Task.FromResult(_next);
        }

        public Task<bool> DeleteCommentAsync(long sessionId, long commentId, string author, CancellationToken cancellationToken) =>
            Task.FromResult(_comments.RemoveAll(c => c.Id == commentId && c.Author == author) > 0);

        public Task<IReadOnlyList<ReviewComment>> ListCommentsAsync(long sessionId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReviewComment>>([.. _comments]);

        public Task<long> AddAskAsync(long sessionId, string? file, int? line, int? endLine, string question, string author, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AnswerAsync(long askId, string answer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewAsk>> ListAsksAsync(long sessionId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ReviewAsk>>([]);

        private Task Update(long id, Func<ReviewSession, ReviewSession> change)
        {
            All[id] = change(All[id]);
            return Task.CompletedTask;
        }
    }
}
