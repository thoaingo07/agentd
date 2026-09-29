using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using Agentd.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class RepositoryStoreTests
{
    private static NpgsqlDataSource s_db = null!;

    [ClassInitialize]
    public static async Task InitAsync(TestContext _) => s_db = await Database.CreateMigratedAsync("repository_store");

    [ClassCleanup]
    public static async Task CleanupAsync() => await s_db.DisposeAsync();

    [TestMethod]
    public async Task Upsert_get_list_update_and_remove()
    {
        var store = new RepositoryStore(s_db);
        var url = RemoteUrl.Parse("git@erm-azdo:v3/ermsystem/Portal/sysmin").Value!;
        var repo = Repository.From(url, null, "develop", "repo:sysmin", ["Portal\\Platform"]);

        Assert.IsTrue((await store.UpsertAsync(repo, default)).IsSuccess);
        var loaded = (await store.GetAsync(RepositoryName.From("sysmin"), default))!;
        Assert.AreEqual(new AzureDevOpsRepo("ermsystem", "Portal", "sysmin"), loaded.AzureDevOps);
        CollectionAssert.AreEqual(new List<string> { "Portal\\Platform" }, loaded.MatchAreaPaths.ToList());

        Assert.IsTrue((await store.UpsertAsync(repo with { BaseBranch = "main" }, default)).IsSuccess);
        Assert.AreEqual("main", (await store.GetAsync(RepositoryName.From("sysmin"), default))!.BaseBranch);

        Assert.IsTrue(await store.RemoveAsync(RepositoryName.From("sysmin"), default));
        Assert.IsNull(await store.GetAsync(RepositoryName.From("sysmin"), default));
        Assert.IsFalse((await store.ListAsync(default)).Any(r => r.Name.Value == "sysmin"));
    }

    [TestMethod]
    public async Task Same_url_under_another_name_is_a_conflict()
    {
        var store = new RepositoryStore(s_db);
        var url = RemoteUrl.Parse("https://dev.azure.com/o/p/_git/dup").Value!;
        await store.UpsertAsync(Repository.From(url, "dup-a", "main", null, []), default);

        var clash = await store.UpsertAsync(Repository.From(url, "dup-b", "main", null, []), default);

        Assert.AreEqual("conflict", clash.Error?.Code);
    }
}
