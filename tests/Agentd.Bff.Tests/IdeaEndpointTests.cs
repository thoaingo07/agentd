using System.Net;
using System.Text.Json;
using Agentd.Application.Abstractions;
using Agentd.Application.Ideas;
using Agentd.Application.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class IdeaEndpointTests
{
    private static readonly IdeaSummary s_idea = new(4, "sysmin", "Dark mode", "tngo", IdeaStatus.Created, "sonnet", "low", 2, [5701, 5702], 6,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));

    private readonly List<int?> _workItems = [];

    [TestMethod]
    public async Task Ideas_are_listed_and_filtered_by_the_work_item_they_created()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var all = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/ideas", UriKind.Relative))).RootElement;
        await client.GetStringAsync(new Uri("/api/ideas?workItem=5702", UriKind.Relative));
        using var invalid = await client.GetAsync(new Uri("/api/ideas?workItem=0", UriKind.Relative));

        CollectionAssert.AreEqual(
            new[] { "id", "repo", "title", "author", "status", "model", "effort", "drafts", "createdWorkItems", "messages", "createdAt", "updatedAt" },
            all[0].EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        CollectionAssert.AreEqual(new int?[] { null, 5702 }, _workItems);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [TestMethod]
    public async Task An_idea_has_its_drafts_and_conversation_or_is_404()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var idea = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/ideas/4", UriKind.Relative))).RootElement;
        using var missing = await client.GetAsync(new Uri("/api/ideas/5", UriKind.Relative));

        CollectionAssert.AreEqual(new[] { "idea", "drafts", "messages" }, idea.EnumerateObject().Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "type", "title", "description", "acceptanceCriteria", "estimate", "parent", "tags" },
            idea.GetProperty("drafts")[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(0, idea.GetProperty("drafts")[1].GetProperty("parent").GetInt32());
        Assert.AreEqual(0, idea.GetProperty("drafts")[1].GetProperty("tags").GetArrayLength(), "no tags is an empty list");
        Assert.AreEqual("out", idea.GetProperty("messages")[1].GetProperty("direction").GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<IQueryHandler<GetIdeas, IReadOnlyList<IdeaSummary>>>(new Ideas(_workItems));
        builder.Services.AddSingleton<IQueryHandler<GetIdea, IdeaDetail?>>(new OneIdea());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Ideas(List<int?> seen) : IQueryHandler<GetIdeas, IReadOnlyList<IdeaSummary>>
    {
        public Task<IReadOnlyList<IdeaSummary>> Handle(GetIdeas query, CancellationToken cancellationToken)
        {
            seen.Add(query.WorkItem);
            return Task.FromResult<IReadOnlyList<IdeaSummary>>([s_idea]);
        }
    }

    private sealed class OneIdea : IQueryHandler<GetIdea, IdeaDetail?>
    {
        public Task<IdeaDetail?> Handle(GetIdea query, CancellationToken cancellationToken) => Task.FromResult(query.Id == 4
            ? new IdeaDetail(s_idea,
                [new WorkItemDraft("User Story", "Dark mode", "As a user…", "- toggle", 3, null, ["ui"]), new WorkItemDraft("Task", "Tokens", null, null, 2, 0, null)],
                [new IdeaMessage("in", "tngo", "dark mode please", DateTimeOffset.UnixEpoch), new IdeaMessage("out", "agentd", "Which pages?", DateTimeOffset.UnixEpoch)])
            : null);
    }
}
