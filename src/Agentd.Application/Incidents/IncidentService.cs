using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Agentd.Application.Events;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Incidents;

/// <summary>
/// What people say about an incident (docs/architect/incident-monitor.md §3.2), in its 🚨 thread or the Inbox:
/// <b>🛠 Fix it</b> files an Azure DevOps Bug tagged for agentd, so a normal job fixes it; <b>🙈 Ignore</b> stops its alerts;
/// <b>reopen</b> watches it again; <b>investigate again</b> reruns the investigation; anything else is a question for the agent.
/// Fix it, Ignore, reopen and investigate need an Operator or Admin. One answer at a time per incident.
/// </summary>
public sealed partial class IncidentService(
    IIncidentStore store,
    IncidentInvestigator investigator,
    IRepositoryRegistry repositories,
    IOptions<IncidentMonitorOptions> options,
    IOptions<Jobs.JobOptions> jobs,
    ILogger<IncidentService> logger,
    IWorkItemSource? workItems = null) : IDisposable
{
    public const string LabelFix = "🛠 Fix it";
    public const string LabelIgnore = "🙈 Ignore";

    /// <summary>The investigation's question: file a Bug, or stop the alerts.</summary>
    public static readonly IReadOnlyList<MessageOption> Choices = [new("incident-fix", LabelFix), new("incident-ignore", LabelIgnore)];

    private const int StackLines = 30;

    private readonly ConcurrentDictionary<long, SemaphoreSlim> _answers = new();

    public void Dispose()
    {
        foreach (var answer in _answers.Values)
        {
            answer.Dispose();
        }
    }

    /// <summary>Handles one message or button about the incident; <paramref name="canAct"/>: the author is an Operator or Admin. Returns what happened.</summary>
    public async Task<string> HandleAsync(Incident incident, string author, bool canAct, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(text);
        var choice = Parse(text);
        if (choice is Choice.Question)
        {
            investigator.Ask(incident.Id, author, text);
            return "asked";
        }

        if (!canAct)
        {
            await investigator.PostAsync(incident, new OutboundMessage(MessageKind.Info, $"🔒 Only Operators and Admins can do that, {author}. You can still ask me about it."), ct)
                .ConfigureAwait(false);
            return "denied";
        }

        var answer = _answers.GetOrAdd(incident.Id, _ => new SemaphoreSlim(1, 1));
        await answer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            incident = await store.GetAsync(incident.Id, ct).ConfigureAwait(false) ?? incident;   // as it is now, after any answer before this one
            switch (choice)
            {
                case Choice.Fix:
                    return await FixAsync(incident, author, ct).ConfigureAwait(false);
                case Choice.Ignore:
                    await store.SetStatusAsync(incident.Id, IncidentStatus.Ignored, null, ct).ConfigureAwait(false);
                    await PostAsync(incident, $"🙈 Ignored by {author}: I'll keep counting it without alerts. Say **reopen** to watch it again.", ct).ConfigureAwait(false);
                    return "ignored";
                case Choice.Reopen:
                    if (incident.Status is not (IncidentStatus.Ignored or IncidentStatus.Resolved)
                        || !await store.SetStatusAsync(incident.Id, IncidentStatus.Open, incident.Status, ct).ConfigureAwait(false))
                    {
                        await PostAsync(incident, $"Incident #{incident.Id} is {incident.Status.ToLowerInvariant()}; there's nothing to reopen.", ct).ConfigureAwait(false);
                        return "unchanged";
                    }

                    await PostAsync(incident, $"👀 Reopened by {author}: new occurrences alert again.", ct).ConfigureAwait(false);
                    return "reopened";
                default:   // investigate again (also after a restart cut one off)
                    await store.SetStatusAsync(incident.Id, IncidentStatus.Open, IncidentStatus.Investigating, ct).ConfigureAwait(false);
                    investigator.Investigate(incident.Id);
                    await PostAsync(incident, $"🔎 Investigating incident #{incident.Id} again ({author}).", ct).ConfigureAwait(false);
                    return "investigating";
            }
        }
        finally
        {
            answer.Release();
        }
    }

    /// <summary>Files the Bug once: a second Fix it names the one already filed.</summary>
    private async Task<string> FixAsync(Incident incident, string author, CancellationToken ct)
    {
        if (incident.WorkItemId is { } filed)
        {
            await PostAsync(incident, $"🛠 Already filed as Bug #{filed}; agentd's job for it is on its way.", ct).ConfigureAwait(false);
            return "filed";
        }

        var app = options.Value.Apps.FirstOrDefault(a => string.Equals(a.Name, incident.App, StringComparison.OrdinalIgnoreCase));
        var repo = app is null ? null : await repositories.GetAsync(RepositoryName.From(app.Repository), ct).ConfigureAwait(false);
        if (workItems is null || app is null || repo is null)
        {
            await PostAsync(incident, workItems is null ? "⚠️ I can't file a Bug: Azure DevOps isn't configured."
                : $"⚠️ I can't file a Bug: app `{incident.App}` or its repository isn't configured anymore.", ct).ConfigureAwait(false);
            return "unavailable";
        }

        var match = repo.MatchTag ?? $"repo:{repo.Name}";
        CreatedWorkItem created;
        try
        {
            var title = $"[{incident.App}] {incident.Title}" + (string.IsNullOrWhiteSpace(incident.Operation) ? string.Empty : $" in {incident.Operation}");
            created = await workItems.CreateAsync(new NewWorkItem("Bug", title.Length <= 250 ? title : title[..249] + "…", ReproSteps(app, incident, author), null,
                [jobs.Value.Tag, match, "incident"], null, null, repo.MatchAreaPaths.Count > 0 ? repo.MatchAreaPaths[0] : null), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFileFailed(logger, ex, incident.Id);
            await PostAsync(incident, $"⚠️ Azure DevOps refused the Bug ({ex.Message}). Try **🛠 Fix it** again.", ct).ConfigureAwait(false);
            return "failed";
        }

        var kept = await store.SetWorkItemAsync(incident.Id, created.Id, ct).ConfigureAwait(false) ?? created.Id;
        await investigator.PostAsync(incident, new OutboundMessage(MessageKind.Result,
            $"🛠 **Filed Bug #{kept}**{(created.Url is { } url && kept == created.Id ? $" ({url})" : string.Empty)} for {author}. It's tagged `{jobs.Value.Tag}` and `{match}`, " +
            "so agentd picks it up at its next poll, plans the fix and opens a PR. This incident is resolved when that job is done."), ct).ConfigureAwait(false);
        return "filed";
    }

    /// <summary>The Bug's repro steps: the analysis, what the telemetry says, a link to it. Redacted.</summary>
    internal static string ReproSteps(MonitoredApp app, Incident incident, string author)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"agentd's incident monitor found this in {app.Name}'s Application Insights (incident #{incident.Id}); {author} asked for a fix.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"{incident.Title}: {incident.Message ?? "(no message)"}\n");
        text.Append(CultureInfo.InvariantCulture, $"{incident.TotalCount} occurrence(s) from {incident.FirstSeen:yyyy-MM-dd HH:mm} to {incident.LastSeen:yyyy-MM-dd HH:mm} UTC");
        text.Append(string.IsNullOrWhiteSpace(incident.Operation) ? ".\n" : $", operation {incident.Operation}.\n");
        if (incident.Sample.Stack?.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(StackLines).ToList() is { Count: > 0 } stack)
        {
            text.Append("\nStack:\n").AppendJoin('\n', stack).Append('\n');
        }

        if (incident.Analysis is { Length: > 0 } analysis)
        {
            text.Append("\nInvestigation:\n").Append(analysis).Append('\n');
        }

        text.Append(CultureInfo.InvariantCulture, $"\nFailures in the Azure portal: https://portal.azure.com/#resource{app.ResourceId}/failures");
        return SecretRedactor.Redact(text.ToString());
    }

    private Task PostAsync(Incident incident, string text, CancellationToken ct) => investigator.PostAsync(incident, new OutboundMessage(MessageKind.Info, text), ct);

    private static Choice Parse(string text) => text.Trim().TrimEnd('.', '!').ToLowerInvariant() switch
    {
        "1" or "fix" or "fix it" or "🛠 fix it" => Choice.Fix,
        "2" or "ignore" or "🙈 ignore" => Choice.Ignore,
        "reopen" or "unignore" or "watch again" => Choice.Reopen,
        "investigate" or "investigate again" or "retry" => Choice.Investigate,
        _ => Choice.Question,
    };

    private enum Choice
    {
        Question,
        Fix,
        Ignore,
        Reopen,
        Investigate,
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {IncidentId}: filing the Bug failed")]
    private static partial void LogFileFailed(ILogger logger, Exception exception, long incidentId);
}
