using Agentd.Application.Repositories;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Tests.Repositories;

[TestClass]
public sealed class AddRepositoryTests
{
    private readonly FakeRegistry _registry = new();
    private readonly FakeGitRemote _remote = new();
    private readonly FakeWorktrees _worktrees = new();

    public AddRepositoryTests() => _registry.Repositories.Clear();

    private AddRepositoryHandler Handler() => new(_registry, _remote, _worktrees);

    [TestMethod]
    public async Task Registers_by_url_detects_the_default_branch_and_clones()
    {
        var result = await Handler().Handle(new AddRepository("git@erm-azdo:v3/ermsystem/Portal/sysmin"), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.ToString());
        var repo = result.Value!;
        Assert.AreEqual("sysmin", repo.Name.Value);
        Assert.AreEqual("develop", repo.BaseBranch);
        Assert.AreEqual("repo:sysmin", repo.MatchTag, "a default tag rule makes the registration usable immediately");
        Assert.AreEqual("sysmin", _worktrees.Cloned.Single());
    }

    [TestMethod]
    public async Task Explicit_name_branch_and_area_path_win()
    {
        var result = await Handler().Handle(
            new AddRepository("https://dev.azure.com/ermsystem/Portal/_git/sysmin", Name: "portal-sysmin", BaseBranch: "main", MatchAreaPaths: ["Portal\\Admin"]),
            CancellationToken.None);

        var repo = result.Value!;
        Assert.AreEqual("portal-sysmin", repo.Name.Value);
        Assert.AreEqual("main", repo.BaseBranch);
        Assert.IsNull(repo.MatchTag);
        CollectionAssert.AreEqual(new List<string> { "Portal\\Admin" }, repo.MatchAreaPaths.ToList());
    }

    [TestMethod]
    public async Task Unreachable_remote_is_a_validation_error_and_registers_nothing()
    {
        _remote.Unreachable = true;

        var result = await Handler().Handle(new AddRepository("git@erm-azdo:v3/ermsystem/Portal/sysmin"), CancellationToken.None);

        Assert.AreEqual("validation", result.Error?.Code);
        StringAssert.Contains(result.Error!.Message, "publickey");
        Assert.IsEmpty(_registry.Repositories);
    }

    [TestMethod]
    public async Task Unsupported_url_is_rejected() =>
        Assert.AreEqual("validation", (await Handler().Handle(new AddRepository("https://example.com/x"), CancellationToken.None)).Error?.Code);

    [TestMethod]
    public async Task Same_url_under_another_name_is_a_conflict()
    {
        await Handler().Handle(new AddRepository("git@erm-azdo:v3/ermsystem/Portal/sysmin"), CancellationToken.None);

        var second = await Handler().Handle(new AddRepository("git@erm-azdo:v3/ermsystem/Portal/sysmin", Name: "other"), CancellationToken.None);

        Assert.AreEqual("conflict", second.Error?.Code);
    }

    [TestMethod]
    public async Task Remove_unregisters_or_reports_not_found()
    {
        await Handler().Handle(new AddRepository("git@erm-azdo:v3/ermsystem/Portal/sysmin"), CancellationToken.None);
        var remove = new RemoveRepositoryHandler(_registry);

        Assert.IsTrue((await remove.Handle(new RemoveRepository(RepositoryName.From("sysmin")), CancellationToken.None)).IsSuccess);
        Assert.AreEqual("not_found", (await remove.Handle(new RemoveRepository(RepositoryName.From("sysmin")), CancellationToken.None)).Error?.Code);
    }

    [TestMethod]
    public async Task Seeding_registers_configured_repositories_once()
    {
        var seeds = new RepositorySeedOptions();
        seeds.Items.Add(new RepositorySeed { Url = "git@erm-azdo:v3/ermsystem/Portal/sysmin", MatchTag = "repo:sysmin" });
        seeds.Items.Add(new RepositorySeed { Url = "not a url" });
        var handler = new SeedRepositoriesHandler(_registry, Handler(), Microsoft.Extensions.Options.Options.Create(seeds));

        var first = (await handler.Handle(new SeedRepositories(), CancellationToken.None)).Value!;
        var second = (await handler.Handle(new SeedRepositories(), CancellationToken.None)).Value!;

        CollectionAssert.AreEqual(new[] { "sysmin" }, first.Seeded.ToArray());
        Assert.HasCount(1, first.Errors);
        Assert.IsEmpty(second.Seeded, "already registered with the same settings");
        Assert.AreEqual("develop", _registry.Repositories.Single().BaseBranch);
    }
}
