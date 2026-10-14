using Agentd.Application.Events;
using Agentd.Application.Incidents;
using Agentd.Bff.Http;
using Agentd.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// The Incidents page (docs/architect/incident-monitor.md §4): what the incident monitor found in Application Insights.
/// The page's actions and questions are Inbox replies (<c>POST /api/inbox/incident/{id}/reply</c>), exactly like the 🚨 thread.
/// Occurrences are read live from the telemetry, redacted, for Operators and Admins.
/// </summary>
public static class IncidentEndpoints
{
    public const int MaxIncidents = 500;
    public const int MaxOccurrences = 20;

    private static readonly string[] s_statuses =
        [IncidentStatus.Open, IncidentStatus.Investigating, IncidentStatus.Fixing, IncidentStatus.Resolved, IncidentStatus.Ignored];

    public static RouteGroupBuilder MapIncidents(this RouteGroupBuilder api)
    {
        var incidents = api.MapGroup("/incidents");

        incidents.MapGet("/", async Task<IResult> (string? status, int? limit, [FromServices] IIncidentStore store, CancellationToken ct) =>
            status is not null && !s_statuses.Contains(status)
                ? DomainError.Validation($"status is one of {string.Join(", ", s_statuses)}.").ToProblem()
                : TypedResults.Ok((await store.ListAsync(status, Math.Clamp(limit ?? 200, 1, MaxIncidents), ct).ConfigureAwait(false)).Select(IncidentSummaryVm.From).ToList()))
            .WithName("GetIncidents").Produces<List<IncidentSummaryVm>>().ProducesProblem(StatusCodes.Status400BadRequest);

        incidents.MapGet("/{id:long}", async Task<IResult> (long id, [FromServices] IIncidentStore store, [FromServices] IOptions<IncidentMonitorOptions> options,
            CancellationToken ct) =>
            await store.GetAsync(id, ct).ConfigureAwait(false) is { } incident
                ? TypedResults.Ok(IncidentDetailVm.From(incident, App(options.Value, incident)))
                : DomainError.NotFound($"Incident {id}").ToProblem())
            .WithName("GetIncident").Produces<IncidentDetailVm>().ProducesProblem(StatusCodes.Status404NotFound);

        incidents.MapGet("/{id:long}/occurrences", async Task<IResult> (long id, [FromServices] IIncidentStore store, [FromServices] ITelemetrySource telemetry,
            [FromServices] IOptions<IncidentMonitorOptions> options, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct).ConfigureAwait(false) is not { } incident)
            {
                return DomainError.NotFound($"Incident {id}").ToProblem();
            }

            if (App(options.Value, incident) is not { } app)
            {
                return DomainError.Conflict($"App '{incident.App}' isn't monitored anymore.").ToProblem();
            }

            // The last week before it was last seen (or since it started), like the investigation's day but wider.
            var since = incident.LastSeen - TimeSpan.FromDays(7) > incident.FirstSeen ? incident.LastSeen - TimeSpan.FromDays(7) : incident.FirstSeen;
            try
            {
                var found = await telemetry.OccurrencesAsync(app, incident, since.AddMinutes(-1), MaxOccurrences, ct).ConfigureAwait(false);
                return TypedResults.Ok(found.Select(IncidentOccurrenceVm.From).ToList());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return TypedResults.Problem($"Application Insights didn't answer: {SecretRedactor.Redact(ex.Message)}", statusCode: StatusCodes.Status502BadGateway,
                    title: "Telemetry unavailable");
            }
        }).RequireAuthorization(p => p.RequireRole("Operator", "Admin"))
            .WithName("GetIncidentOccurrences").Produces<List<IncidentOccurrenceVm>>().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status502BadGateway);

        return api;
    }

    private static MonitoredApp? App(IncidentMonitorOptions options, Incident incident) =>
        options.Apps.FirstOrDefault(a => string.Equals(a.Name, incident.App, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An incident in the list: one error group of one app, its status and counts, and the Bug filed for it.</summary>
public sealed record IncidentSummaryVm(long Id, string App, string Kind, string Title, string? Message, string? Operation, string Status, long TotalCount,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int? WorkItemId, bool Analyzed)
{
    public static IncidentSummaryVm From(Incident i)
    {
        ArgumentNullException.ThrowIfNull(i);
        return new(i.Id, i.App, i.Kind, i.Title, i.Message, i.Operation, i.Status, i.TotalCount, i.FirstSeen, i.LastSeen, i.WorkItemId, i.Analysis is not null);
    }
}

/// <summary>
/// An incident whole: the recorded sample (top of the stack, result code), the investigation's analysis (Markdown), the app's
/// repository and Azure portal link (null when the app isn't monitored anymore), and whether it has a chat thread.
/// </summary>
public sealed record IncidentDetailVm(IncidentSummaryVm Summary, string? Stack, string? ResultCode, string? OperationId, string? Analysis, DateTimeOffset? AnalyzedAt,
    string? Repository, string? PortalUrl, bool HasThread)
{
    public static IncidentDetailVm From(Incident i, MonitoredApp? app)
    {
        ArgumentNullException.ThrowIfNull(i);
        return new(IncidentSummaryVm.From(i), Redact(i.Sample.Stack), i.Sample.ResultCode, i.Sample.OperationId, i.Analysis, i.AnalyzedAt, app?.Repository,
            app is null ? null : $"https://portal.azure.com/#resource{app.ResourceId}/failures", i.ThreadId is not null);
    }

    internal static string? Redact(string? text) => text is null ? null : SecretRedactor.Redact(text);
}

/// <summary>One occurrence from the telemetry, redacted: when, the message, the stack, the request and its custom dimensions (JSON).</summary>
public sealed record IncidentOccurrenceVm(DateTimeOffset At, string? Message, string? Stack, string? Operation, string? OperationId, string? Url, string? ResultCode,
    string? Properties)
{
    public static IncidentOccurrenceVm From(ErrorOccurrence o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new(o.At, IncidentDetailVm.Redact(o.Message), IncidentDetailVm.Redact(o.Stack), o.Operation, o.OperationId, IncidentDetailVm.Redact(o.Url), o.ResultCode,
            IncidentDetailVm.Redact(o.Properties));
    }
}
