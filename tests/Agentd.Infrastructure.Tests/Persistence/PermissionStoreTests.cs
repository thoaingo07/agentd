using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Persistence.Repositories;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class PermissionStoreTests
{
    private static int s_next;

    [TestMethod]
    public async Task Racing_answers_decide_a_request_once()
    {
        await using var db = await Database.CreateMigratedAsync($"permissions_{Interlocked.Increment(ref s_next)}");
        var (store, job) = await SetupAsync(db);

        for (var round = 0; round < 20; round++)
        {
            var id = await store.InsertAsync(job, "Bash", "npm install", ["Bash(npm install:*)"], default);
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() =>
                store.DecideAsync(id, i % 2 == 0 ? "allowed" : "denied", i % 2 == 0 ? "once" : null, $"user{i}", RepositoryName.From("sysmin"), default))));

            Assert.AreEqual(1, results.Count(r => r is not null), $"round {round}: exactly one caller decides");
            var stored = await store.GetAsync(id, default);
            Assert.AreEqual(results.Single(r => r is not null)!.DecidedBy, stored!.DecidedBy, "the winner's decision is stored");
        }
    }

    [TestMethod]
    public async Task Allowing_for_the_job_or_the_repo_remembers_the_rule_keys()
    {
        await using var db = await Database.CreateMigratedAsync($"permissions_{Interlocked.Increment(ref s_next)}");
        var (store, job) = await SetupAsync(db);
        var (_, other) = await SetupAsync(db, 9001);
        var repo = RepositoryName.From("sysmin");

        var once = await store.InsertAsync(job, "Bash", "python3 x.py", ["Bash(python3:*)"], default);
        await store.DecideAsync(once, "allowed", "once", "tngo", repo, default);
        var forJob = await store.InsertAsync(job, "Bash", "npm install", ["Bash(npm install:*)"], default);
        await store.DecideAsync(forJob, "allowed", "job", "tngo", repo, default);
        var always = await store.InsertAsync(job, "Bash", "git fetch", ["Bash(git fetch:*)"], default);
        await store.DecideAsync(always, "allowed", "repo", "tngo", repo, default);

        CollectionAssert.AreEquivalent(new[] { "Bash(npm install:*)", "Bash(git fetch:*)" }, (await store.RuleKeysAsync(repo, job, default)).ToArray(), "once isn't remembered");
        CollectionAssert.AreEquivalent(new[] { "Bash(git fetch:*)" }, (await store.RuleKeysAsync(repo, other, default)).ToArray(), "another job of the repo only gets the repo rule");
        Assert.IsEmpty(await store.ListPendingAsync(job, default));

        await using var count = db.CreateCommand("SELECT count(*) FROM agentd.events WHERE type IN ('permission.requested', 'permission.decided')");
        Assert.AreEqual(6L, (long)(await count.ExecuteScalarAsync())!, "every request and decision is in the event log");
    }

    [TestMethod]
    public async Task Pending_counts_and_rules_can_be_listed_and_a_rule_revoked_once()
    {
        await using var db = await Database.CreateMigratedAsync($"permissions_{Interlocked.Increment(ref s_next)}");
        var (store, job) = await SetupAsync(db);
        var repo = RepositoryName.From("sysmin");
        await store.InsertAsync(job, "Bash", "make", ["Bash(make:*)"], default);
        var forJob = await store.InsertAsync(job, "Bash", "npm install", ["Bash(npm install:*)"], default);
        await store.DecideAsync(forJob, "allowed", "job", "tngo", repo, default);
        var always = await store.InsertAsync(job, "WebFetch", "https://docs.npmjs.com", ["WebFetch(domain:docs.npmjs.com)"], default);
        await store.DecideAsync(always, "allowed", "repo", "tngo", repo, default);

        Assert.AreEqual(1, (await store.PendingCountsAsync(default))[job]);
        var rules = await store.ListRulesAsync(default);
        Assert.AreEqual(("WebFetch(domain:docs.npmjs.com)", (JobId?)null, "tngo"), (rules[0].RuleKey, rules[0].JobId, rules[0].CreatedBy), "newest first; a repo rule has no job");
        Assert.AreEqual(("Bash(npm install:*)", (JobId?)job), (rules[1].RuleKey, rules[1].JobId));

        var revoked = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.DeleteRuleAsync(rules[0].Id, "admin", default))));

        Assert.AreEqual(1, revoked.Count(r => r), "exactly one caller revokes it");
        CollectionAssert.AreEqual(new[] { "Bash(npm install:*)" }, (await store.RuleKeysAsync(repo, job, default)).ToArray());
        await using var count = db.CreateCommand("SELECT count(*) FROM agentd.events WHERE type = 'permission.revoked'");
        Assert.AreEqual(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    private static async Task<(PermissionStore Store, JobId Job)> SetupAsync(Npgsql.NpgsqlDataSource db, int workItem = 5615)
    {
        var job = Job.Create(WorkItemId.From(workItem), RepositoryName.From("sysmin"), "Tailwind v4", new Clock());
        await new JobRepository(db, new Clock()).AddAsync(job, default);
        return (new PermissionStore(db), job.Id);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
