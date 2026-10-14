using System.Globalization;
using System.Text;
using Agentd.Application.Events;
using Agentd.Application.Inbox;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Incidents;

/// <summary>
/// One pass of the incident monitor (docs/architect/incident-monitor.md): for each app, the exceptions and failed requests
/// since its last pass, recorded by fingerprint. A new incident, or a resolved one that's back, gets an alert: a 🚨 thread on
/// the default chat provider and the Inbox (the Inbox alone without chat), then an investigation (<see cref="IncidentInvestigator"/>).
/// Every text goes through <see cref="SecretRedactor"/>.
/// An app that can't be read is logged and tried again next pass from the same point; the others go on.
/// </summary>
public sealed partial class IncidentMonitor(
    IOptions<IncidentMonitorOptions> options,
    ITelemetrySource telemetry,
    IIncidentStore store,
    IMessagingProviderRegistry providers,
    IOptions<MessagingOptions> messaging,
    IClock clock,
    ILogger<IncidentMonitor> logger,
    IInbox? inbox = null,
    IncidentInvestigator? investigator = null,
    IWorkItemHistory? history = null)
{
    /// <summary>The longest window one pass reads, after agentd was down for a while.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(1);

    private const int StackLines = 8;
    private const int MessageLength = 500;

    /// <summary>Reads every app once; returns how many alerts went out.</summary>
    public async Task<int> PassAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        try
        {
            await ResolveFixedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResolveFailed(logger, ex);
        }

        var alerts = 0;
        foreach (var app in o.Apps)
        {
            try
            {
                alerts += await CheckAsync(app, o, o.MaxNewPerPass - alerts, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogAppFailed(logger, ex, app.Name);
            }
        }

        return alerts;
    }

    /// <summary>A Fixing incident whose Bug's latest job is Done is Resolved; if it recurs, recording reopens it ("is back").</summary>
    private async Task ResolveFixedAsync(CancellationToken ct)
    {
        if (history is null)
        {
            return;
        }

        foreach (var incident in await store.ListAsync(IncidentStatus.Fixing, 200, ct).ConfigureAwait(false))
        {
            var jobs = incident.WorkItemId is { } bug ? await history.ListJobsAsync(WorkItemId.From(bug), ct).ConfigureAwait(false) : [];
            if (jobs.Count == 0 || jobs[^1] is not { State: Domain.Jobs.JobState.Done } job
                || !await store.SetStatusAsync(incident.Id, IncidentStatus.Resolved, IncidentStatus.Fixing, ct).ConfigureAwait(false))
            {
                continue;
            }

            var message = new OutboundMessage(MessageKind.Result, $"✅ **Incident #{incident.Id} is resolved**: agentd's job for Bug #{incident.WorkItemId} is done" +
                (job.PullRequest is { } pr ? $" (PR {pr.Value})" : string.Empty) + ". If it happens again, I'll reopen it here.");
            if (investigator is not null)
            {
                await investigator.PostAsync(incident, message, ct).ConfigureAwait(false);
            }
            else
            {
                await inbox.TryAddAsync(new InboxEntry(InboxSources.Incident, incident.Id, Ideas.IdeaService.Title($"🚨 {incident.App}: {incident.Title}"), null, message), logger, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<int> CheckAsync(MonitoredApp app, IncidentMonitorOptions o, int budget, CancellationToken ct)
    {
        var to = clock.UtcNow;
        var from = await store.GetCursorAsync(app.Name, ct).ConfigureAwait(false) ?? to - o.FirstWindow;
        if (from < to - MaxWindow)
        {
            from = to - MaxWindow;
        }

        if (from >= to)
        {
            return 0;
        }

        var alerts = 0;
        foreach (var group in await telemetry.ErrorsAsync(app, from, to, ct).ConfigureAwait(false))
        {
            if (await store.RecordAsync(app.Name, group, o.MinCount, ct).ConfigureAwait(false) is not { } recorded
                || !(recorded.Created || recorded.Reopened) || alerts >= budget)
            {
                continue;
            }

            await AlertAsync(app, group, recorded, to - from, ct).ConfigureAwait(false);
            investigator?.Investigate(recorded.Id);   // in the background: the analysis follows the alert
            alerts++;
        }

        await store.SetCursorAsync(app.Name, to, ct).ConfigureAwait(false);
        return alerts;
    }

    private async Task AlertAsync(MonitoredApp app, ErrorGroup group, IncidentRecorded recorded, TimeSpan window, CancellationToken ct)
    {
        var title = $"🚨 {app.Name}: {group.Title}";
        var message = new OutboundMessage(MessageKind.Info, SecretRedactor.Redact(Describe(app, group, recorded, window)));
        await inbox.TryAddAsync(new InboxEntry(InboxSources.Incident, recorded.Id, Ideas.IdeaService.Title(title), null, message), logger, ct).ConfigureAwait(false);
        try
        {
            var incident = await store.GetAsync(recorded.Id, ct).ConfigureAwait(false);
            var thread = incident is { Provider: { } key, ThreadId: { } id } && ProviderKey.Create(key) is { IsSuccess: true } parsed && providers.TryResolve(parsed.Value) is not null
                ? new ConversationRef(parsed.Value, id, incident.SpaceId)
                : null;
            if (thread is null)
            {
                if (Provider() is not { } provider)
                {
                    return;   // no chat: the Inbox has it
                }

                var opening = new OutboundMessage(MessageKind.Info, SecretRedactor.Redact($"🚨 **{app.Name}**: {group.Title}"));
                thread = await provider.OpenConversationAsync(
                    new ConversationSpec(default, default, title, RepositoryName.From(app.Repository), opening, Ideas.IdeaService.Title(SecretRedactor.Redact(title))), ct).ConfigureAwait(false);
                await store.SetThreadAsync(recorded.Id, thread.Provider.Value, thread.ExternalConversationId, thread.ExternalSpaceId, ct).ConfigureAwait(false);
            }

            var target = providers.Resolve(thread.Provider);
            foreach (var part in MessageChunker.Prepare(message, target.Capabilities))
            {
                await target.SendAsync(thread, part, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostFailed(logger, ex, recorded.Id);
        }
    }

    /// <summary>The default chat provider (<see cref="MessagingOptions.DefaultProviders"/>' first enabled one, else the first enabled), or null.</summary>
    private IMessagingProvider? Provider()
    {
        var defaults = messaging.Value.DefaultProviders;
        return providers.Enabled.FirstOrDefault(p => defaults.Contains(p.Key.Value, StringComparer.OrdinalIgnoreCase)) ?? (providers.Enabled.Count > 0 ? providers.Enabled[0] : null);
    }

    internal static string Describe(MonitoredApp app, ErrorGroup group, IncidentRecorded recorded, TimeSpan window)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{(recorded.Reopened ? "🔁 **Incident #" + recorded.Id + " is back**" : "🚨 **New incident #" + recorded.Id + "**")} in **{app.Name}** ({group.Kind})\n");
        text.Append(CultureInfo.InvariantCulture, $"**{group.Title}**\n");
        if (!string.IsNullOrWhiteSpace(group.Message))
        {
            var message = group.Message.ReplaceLineEndings(" ").Trim();
            text.Append(message.Length <= MessageLength ? message : message[..(MessageLength - 1)] + "…").Append('\n');
        }

        text.Append(CultureInfo.InvariantCulture, $"\n• **{group.Count}** in the last {Span(window)} · {group.Users} user(s)");
        if (!string.IsNullOrWhiteSpace(group.Operation))
        {
            text.Append(CultureInfo.InvariantCulture, $" · operation `{group.Operation}`");
        }

        if (!string.IsNullOrWhiteSpace(group.Sample.ResultCode))
        {
            text.Append(CultureInfo.InvariantCulture, $" · result {group.Sample.ResultCode}");
        }

        text.Append(CultureInfo.InvariantCulture, $"\n• first {group.FirstSeen:yyyy-MM-dd HH:mm} UTC, last {group.LastSeen:yyyy-MM-dd HH:mm} UTC");
        if (!string.IsNullOrWhiteSpace(group.Sample.OperationId))
        {
            text.Append(CultureInfo.InvariantCulture, $" · operation id `{group.Sample.OperationId}`");
        }

        var stack = group.Sample.Stack?.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(StackLines).ToList();
        if (stack is { Count: > 0 })
        {
            text.Append("\n```\n").AppendJoin('\n', stack.Select(l => l.Replace("```", "'''", StringComparison.Ordinal))).Append("\n```");
        }

        text.Append(CultureInfo.InvariantCulture, $"\n[Failures in the Azure portal](https://portal.azure.com/#resource{app.ResourceId}/failures)");
        return text.ToString();
    }

    private static string Span(TimeSpan window) =>
        window >= TimeSpan.FromHours(1) ? string.Create(CultureInfo.InvariantCulture, $"{window.TotalHours:0.#} h") : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)window.TotalMinutes)} min");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident monitor: reading {App} failed; trying again next pass from the same point")]
    private static partial void LogAppFailed(ILogger logger, Exception exception, string app);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident monitor: checking fixed incidents failed; trying again next pass")]
    private static partial void LogResolveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {IncidentId}: posting the alert failed; the Inbox has it")]
    private static partial void LogPostFailed(ILogger logger, Exception exception, long incidentId);
}
