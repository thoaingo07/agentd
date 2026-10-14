using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Agentd.Application.Events;
using Agentd.Application.Ideas;
using Agentd.Application.Inbox;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Incidents;

/// <summary>
/// Investigates incidents (docs/architect/incident-monitor.md §3): a read-only agent turn (<see cref="ThreadTurnKind.Incident"/>) in a
/// checkout of the app's repository, from telemetry agentd reads itself (the agent gets no Azure access). The analysis is kept with
/// the incident and posted in its 🚨 thread and the Inbox. Every telemetry text goes through <see cref="SecretRedactor"/> before it
/// reaches the prompt or a post. Questions about the incident resume the investigation's session. One turn at a time per
/// incident, at most <see cref="MaxConcurrent"/> at once.
/// </summary>
public sealed partial class IncidentInvestigator(
    IIncidentStore store,
    ITelemetrySource telemetry,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    IMessagingProviderRegistry providers,
    IOptions<IncidentMonitorOptions> options,
    ILogger<IncidentInvestigator> logger,
    IInbox? inbox = null) : IDisposable
{
    public const int MaxConcurrent = 2;

    /// <summary>How many recent occurrences the prompt carries.</summary>
    public const int Occurrences = 3;

    private const int StackLines = 60;
    private const int PropertiesLength = 1500;

    private readonly ConcurrentDictionary<long, SemaphoreSlim> _turns = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    public void Dispose()
    {
        _slots.Dispose();
        foreach (var turn in _turns.Values)
        {
            turn.Dispose();
        }
    }

    /// <summary>Investigates in the background (after the incident's other turns); nothing happens unless it's Open.</summary>
    public void Investigate(long incidentId) => Background(incidentId, ct => InvestigateAsync(incidentId, ct));

    /// <summary>Answers a question about the incident in the background, resuming its investigation's session.</summary>
    public void Ask(long incidentId, string author, string question) => Background(incidentId, ct => AskAsync(incidentId, author, question, ct));

    /// <summary>One turn at a time per incident, at most <see cref="MaxConcurrent"/> in all.</summary>
    private void Background(long incidentId, Func<CancellationToken, Task> work)
    {
        var turn = _turns.GetOrAdd(incidentId, _ => new SemaphoreSlim(1, 1));
        _ = Task.Run(async () =>
        {
            await turn.WaitAsync().ConfigureAwait(false);
            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                await work(CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // a failed turn must not take the monitor or a thread down
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex, incidentId);
            }
            finally
            {
                _slots.Release();
                turn.Release();
            }
        });
    }

    /// <summary>Answers a question in the incident's thread from its investigation's session (investigates first when there's none).</summary>
    public async Task AskAsync(long incidentId, string author, string question, CancellationToken ct)
    {
        if (await store.GetAsync(incidentId, ct).ConfigureAwait(false) is not { } incident)
        {
            return;
        }

        if (incident.Session is not { } session)
        {
            await InvestigateAsync(incidentId, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var app = options.Value.Apps.FirstOrDefault(a => string.Equals(a.Name, incident.App, StringComparison.OrdinalIgnoreCase));
            if (app is null || await repositories.GetAsync(RepositoryName.From(app.Repository), ct).ConfigureAwait(false) is not { } repo)
            {
                await PostAsync(incident, new OutboundMessage(MessageKind.Info, $"⚠️ I can't answer: app `{incident.App}` or its repository isn't configured anymore."), ct).ConfigureAwait(false);
                return;
            }

            var path = await worktrees.CheckoutDetachedAsync(repo, $"incident-{incident.Id}", ct).ConfigureAwait(false);
            var reply = await agent.RunAsync(new BrainstormTurn(incident.Id, path, session, true, SecretRedactor.Redact($"{author} asks: {question.Trim()}"),
                Kind: ThreadTurnKind.Incident), ct).ConfigureAwait(false);
            await PostAsync(incident, new OutboundMessage(MessageKind.Info, reply.Text is { Length: > 0 } text
                ? SecretRedactor.Redact(text.Trim())
                : reply.UsageLimitedUntil is { } until ? $"⏸ The Claude usage limit is reached until {until:HH:mm} UTC. Ask again after that."
                : $"⚠️ I couldn't answer that ({reply.Error ?? "no answer"}). Ask again to retry."), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex, incident.Id);
            await PostAsync(incident, new OutboundMessage(MessageKind.Info, $"⚠️ I couldn't answer that ({ex.GetType().Name}). Ask again to retry."), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs the investigation now: Open → Investigating → Open with the analysis (or Open again with a short error).</summary>
    public async Task InvestigateAsync(long incidentId, CancellationToken ct)
    {
        if (await store.GetAsync(incidentId, ct).ConfigureAwait(false) is not { } incident
            || !await store.SetStatusAsync(incidentId, IncidentStatus.Investigating, IncidentStatus.Open, ct).ConfigureAwait(false))
        {
            return;   // gone, or not Open (ignored, fixing, already investigating)
        }

        try
        {
            var app = options.Value.Apps.FirstOrDefault(a => string.Equals(a.Name, incident.App, StringComparison.OrdinalIgnoreCase));
            var repo = app is null ? null : await repositories.GetAsync(RepositoryName.From(app.Repository), ct).ConfigureAwait(false);
            if (app is null || repo is null)
            {
                await GiveUpAsync(incident, app is null
                    ? $"⚠️ I can't investigate: app `{incident.App}` isn't in Agentd:Monitor:Apps anymore."
                    : $"⚠️ I can't investigate: repository `{app.Repository}` isn't registered (`!repo add <url>`).", ct).ConfigureAwait(false);
                return;
            }

            IReadOnlyList<ErrorOccurrence> occurrences;
            try
            {
                var since = incident.LastSeen - TimeSpan.FromDays(1) > incident.FirstSeen ? incident.LastSeen - TimeSpan.FromDays(1) : incident.FirstSeen;
                occurrences = await telemetry.OccurrencesAsync(app, incident, since.AddMinutes(-1), Occurrences, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTelemetryFailed(logger, ex, incident.Id);
                occurrences = [];   // the incident's own sample still goes in
            }

            var path = await worktrees.CheckoutDetachedAsync(repo, $"incident-{incident.Id}", ct).ConfigureAwait(false);
            var session = Guid.NewGuid();
            var reply = await agent.RunAsync(new BrainstormTurn(incident.Id, path, session, false, Prompt(incident, repo, path, occurrences),
                Kind: ThreadTurnKind.Incident), ct).ConfigureAwait(false);
            if (reply.Text is not { Length: > 0 } text)
            {
                await GiveUpAsync(incident, reply.UsageLimitedUntil is { } until
                    ? $"⏸ The Claude usage limit is reached until {until:HH:mm} UTC. Ask me here after that to investigate incident #{incident.Id}."
                    : $"⚠️ I couldn't finish investigating incident #{incident.Id} ({reply.Error ?? "no answer"}).", ct).ConfigureAwait(false);
                return;
            }

            var analysis = SecretRedactor.Redact(text.Trim());
            await store.SetAnalysisAsync(incident.Id, analysis, session, ct).ConfigureAwait(false);
            await PostAsync(incident, new OutboundMessage(MessageKind.Question, $"🔎 **Investigation of incident #{incident.Id}**\n\n{analysis}\n\n" +
                $"**{IncidentService.LabelFix}** files an Azure DevOps Bug that agentd fixes in a normal job; **{IncidentService.LabelIgnore}** stops the alerts. Or ask me about it here.",
                IncidentService.Choices), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex, incident.Id);
            await GiveUpAsync(incident, $"⚠️ I couldn't investigate incident #{incident.Id} ({ex.GetType().Name}). Ask me here to try again.", ct).ConfigureAwait(false);
        }
    }

    /// <summary>The investigation's prompt: the incident, its latest occurrences whole, and where the code is. Redacted.</summary>
    internal static string Prompt(Incident incident, Repository repo, string path, IReadOnlyList<ErrorOccurrence> occurrences)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Investigate incident #{incident.Id} in the app **{incident.App}** ({incident.Kind}): **{incident.Title}**\n");
        if (!string.IsNullOrWhiteSpace(incident.Message))
        {
            text.Append(CultureInfo.InvariantCulture, $"Message: {incident.Message.Trim()}\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"Operation: {incident.Operation ?? "(none)"} · {incident.TotalCount} occurrence(s), first {incident.FirstSeen:yyyy-MM-dd HH:mm} UTC, last {incident.LastSeen:yyyy-MM-dd HH:mm} UTC\n");
        text.Append(CultureInfo.InvariantCulture, $"The code: repository `{repo.Name}` at `{repo.BaseBranch}`, a read-only checkout at {path}\n");
        if (occurrences.Count == 0)
        {
            text.Append("\nagentd couldn't read the occurrences; the latest one it recorded:\n");
            AppendStack(text, incident.Sample.Stack);
        }

        for (var i = 0; i < occurrences.Count; i++)
        {
            var o = occurrences[i];
            text.Append(CultureInfo.InvariantCulture, $"\n### Occurrence {i + 1} · {o.At:yyyy-MM-dd HH:mm:ss} UTC\n");
            if (!string.IsNullOrWhiteSpace(o.Message))
            {
                text.Append(CultureInfo.InvariantCulture, $"Message: {o.Message.Trim()}\n");
            }

            if (o.Url is not null || o.ResultCode is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"Request: {o.Url ?? o.Operation ?? "?"} → {o.ResultCode ?? "?"}\n");
            }

            if (o.OperationId is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"Operation id: {o.OperationId}\n");
            }

            AppendStack(text, o.Stack);
            if (o.Properties is { Length: > 0 } p)
            {
                text.Append("Custom dimensions: ").Append(p.Length <= PropertiesLength ? p : p[..PropertiesLength] + "…").Append('\n');
            }
        }

        text.Append("\nFind the cause in the code and answer as your instructions say.");
        return SecretRedactor.Redact(text.ToString());
    }

    private static void AppendStack(StringBuilder text, string? stack)
    {
        var lines = stack?.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(StackLines).ToList();
        if (lines is { Count: > 0 })
        {
            text.Append("```\n").AppendJoin('\n', lines.Select(l => l.Replace("```", "'''", StringComparison.Ordinal))).Append("\n```\n");
        }
    }

    private async Task GiveUpAsync(Incident incident, string why, CancellationToken ct)
    {
        await store.SetStatusAsync(incident.Id, IncidentStatus.Open, IncidentStatus.Investigating, ct).ConfigureAwait(false);
        await PostAsync(incident, new OutboundMessage(MessageKind.Info, why), ct).ConfigureAwait(false);
    }

    /// <summary>agentd's message about an incident: in the Inbox always, and in its 🚨 thread when it has one.</summary>
    internal async Task PostAsync(Incident incident, OutboundMessage message, CancellationToken ct)
    {
        await inbox.TryAddAsync(new InboxEntry(InboxSources.Incident, incident.Id, IdeaService.Title($"🚨 {incident.App}: {incident.Title}"), null, message), logger, ct)
            .ConfigureAwait(false);
        if (incident is not { Provider: { } key, ThreadId: { } thread } || ProviderKey.Create(key) is not { IsSuccess: true } provider
            || providers.TryResolve(provider.Value) is not { } target)
        {
            return;
        }

        try
        {
            foreach (var part in MessageChunker.Prepare(message, target.Capabilities))
            {
                await target.SendAsync(new ConversationRef(provider.Value, thread, incident.SpaceId), part, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostFailed(logger, ex, incident.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Incident {IncidentId}: the agent's turn failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, long incidentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {IncidentId}: reading its occurrences failed; investigating from the recorded sample")]
    private static partial void LogTelemetryFailed(ILogger logger, Exception exception, long incidentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {IncidentId}: posting to the thread failed; the Inbox has it")]
    private static partial void LogPostFailed(ILogger logger, Exception exception, long incidentId);
}
