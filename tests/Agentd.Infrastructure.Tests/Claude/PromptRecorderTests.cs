using Agentd.Application.Prompts;
using Agentd.Infrastructure.Claude;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.Claude;

[TestClass]
public sealed class PromptRecorderTests
{
    [TestMethod]
    public async Task A_turns_transcript_keeps_at_most_MaxEvents_and_says_how_many_were_dropped()
    {
        var log = new Log();
        var recorder = new PromptRecorder(log, Options.Create(new PromptLogOptions { MaxEvents = 3 }));
        var events = Enumerable.Range(0, 10).Select(i => new PromptEvent("agent.text", $"{{\"text\":\"{i}\"}}", DateTimeOffset.UnixEpoch)).ToList();

        await recorder.AddEventsAsync(9, events);
        await recorder.AddEventsAsync(null, events);   // not kept: nothing written

        var (id, kept) = log.Added.Single();
        Assert.AreEqual(9, id);
        Assert.HasCount(4, kept);
        StringAssert.Contains(kept[^1].PayloadJson, "7 more events not kept");
    }

    [TestMethod]
    public async Task A_failing_log_never_fails_the_turn()
    {
        var recorder = new PromptRecorder(new Log { Fail = true });

        await recorder.AddEventsAsync(1, [new PromptEvent("agent.text", "{}", DateTimeOffset.UnixEpoch)]);
    }

    private sealed class Log : IPromptLog
    {
        public List<(long Id, IReadOnlyList<PromptEvent> Events)> Added { get; } = [];

        public bool Fail { get; init; }

        public Task AddEventsAsync(long id, IReadOnlyList<PromptEvent> events, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("the database is down");
            }

            Added.Add((id, events));
            return Task.CompletedTask;
        }

        public Task<long> StartAsync(PromptStart prompt, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task FinishAsync(long id, string outcome, string? failure, string? reply, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PromptSummary>> ListAsync(PromptFilter filter, long? before, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PromptDetail?> GetAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
