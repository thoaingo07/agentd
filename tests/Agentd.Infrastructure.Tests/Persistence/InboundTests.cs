using Agentd.Application.Jobs;
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
public sealed class InboundTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private static NpgsqlDataSource s_db = null!;
    private static int s_nextWorkItem = 9000;

    [ClassInitialize]
    public static async Task InitAsync(TestContext _) => s_db = await Database.CreateMigratedAsync("inbound");

    [ClassCleanup]
    public static async Task CleanupAsync() => await s_db.DisposeAsync();

    [TestMethod]
    public async Task A_message_is_claimed_once_and_can_be_released()
    {
        var log = new InboundLog(s_db);

        var claims = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => log.TryRecordAsync(s_discord, "m-1", DateTimeOffset.UtcNow, default)));
        Assert.AreEqual(1, claims.Count(c => c), "exactly one concurrent claim wins");

        await log.SetOutcomeAsync(s_discord, "m-1", "queued", null, null, default);
        Assert.IsFalse(await log.TryRecordAsync(s_discord, "m-1", DateTimeOffset.UtcNow, default));

        await log.ForgetAsync(s_discord, "m-1", default);
        Assert.IsTrue(await log.TryRecordAsync(s_discord, "m-1", DateTimeOffset.UtcNow, default), "released for redelivery");
        Assert.IsTrue(await log.TryRecordAsync(ProviderKey.From("slack"), "m-1", DateTimeOffset.UtcNow, default), "ids are per provider");
    }

    [TestMethod]
    public async Task Racing_replies_resume_a_waiting_job_exactly_once()
    {
        var clock = new Clock();
        var repo = new JobRepository(s_db, clock);
        for (var round = 0; round < 100; round++)
        {
            var job = Job.Create(WorkItemId.From(Interlocked.Increment(ref s_nextWorkItem)), RepositoryName.From("sysmin"), "t", clock);
            await repo.AddAsync(job, default);
            job.BeginPreparing();
            job.Start(new WorktreePath("/wt"), BranchName.From($"ai/{job.WorkItemId}-t"), ClaudeSessionId.New());
            job.AskDeveloper("Which?");
            await repo.SaveAsync(job, default);
            var submit = new SubmitDeveloperMessageHandler(new JobRepository(s_db, clock));

            var results = await Task.WhenAll(
                submit.Handle(new SubmitDeveloperMessage(job.Id, "a", "alice", s_discord), default),
                submit.Handle(new SubmitDeveloperMessage(job.Id, "b", "bob", s_discord), default));

            CollectionAssert.AreEquivalent(new[] { DeveloperMessageOutcome.Resumed, DeveloperMessageOutcome.Queued }, results.Select(r => r.Value).ToArray(), $"round {round}");
            var stored = (await repo.GetAsync(job.Id, default))!;
            Assert.AreEqual(JobState.Running, stored.State);
            Assert.HasCount(2, stored.PendingMessages, "both replies reach the agent");
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
