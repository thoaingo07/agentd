using Agentd.Application.Incidents;

namespace Agentd.Application.Tests.Incidents;

internal sealed class FakeTelemetry : ITelemetrySource
{
    public Dictionary<string, IReadOnlyList<ErrorGroup>> Groups { get; } = [];

    public HashSet<string> Failing { get; } = [];

    public List<(DateTimeOffset Since, DateTimeOffset Until)> Windows { get; } = [];

    public Task<IReadOnlyList<ErrorGroup>> ErrorsAsync(MonitoredApp app, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        if (Failing.Contains(app.Name))
        {
            throw new InvalidOperationException("AuthorizationFailed");
        }

        Windows.Add((since, until));
        return Task.FromResult(Groups.GetValueOrDefault(app.Name) ?? []);
    }

    public List<ErrorOccurrence> Occurrences { get; } = [];

    public bool OccurrencesFail { get; set; }

    public Task<IReadOnlyList<ErrorOccurrence>> OccurrencesAsync(MonitoredApp app, Incident incident, DateTimeOffset since, int max, CancellationToken cancellationToken) =>
        OccurrencesFail ? throw new InvalidOperationException("AuthorizationFailed") : Task.FromResult<IReadOnlyList<ErrorOccurrence>>([.. Occurrences.Take(max)]);
}

/// <summary>The incident routines' rules, in memory.</summary>
internal sealed class FakeIncidentStore : IIncidentStore
{
    public List<Incident> Rows { get; } = [];

    public Dictionary<string, DateTimeOffset> Cursors { get; } = [];

    public Task<IncidentRecorded?> RecordAsync(string app, ErrorGroup group, int minCount, CancellationToken cancellationToken)
    {
        var at = Rows.FindIndex(r => r.App == app && r.Fingerprint == group.Fingerprint);
        if (at < 0)
        {
            if (group.Count < minCount)
            {
                return Task.FromResult<IncidentRecorded?>(null);
            }

            Rows.Add(new Incident(Rows.Count + 1, app, group.Fingerprint, group.Kind, group.Title, group.Message, group.Operation, group.Sample, IncidentStatus.Open,
                group.Count, group.FirstSeen, group.LastSeen, null, null, null, null, DateTimeOffset.UnixEpoch));
            return Task.FromResult<IncidentRecorded?>(new IncidentRecorded(Rows.Count, true, false));
        }

        var row = Rows[at];
        var reopened = row.Status == IncidentStatus.Resolved;
        Rows[at] = row with { TotalCount = row.TotalCount + group.Count, Status = reopened ? IncidentStatus.Open : row.Status };
        return Task.FromResult<IncidentRecorded?>(new IncidentRecorded(row.Id, false, reopened));
    }

    public Task<Incident?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));

    public Task<IReadOnlyList<Incident>> ListAsync(string? status, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetThreadAsync(long id, string provider, string threadId, string? spaceId, CancellationToken cancellationToken)
    {
        var at = Rows.FindIndex(r => r.Id == id);
        Rows[at] = Rows[at] with { Provider = provider, ThreadId = threadId, SpaceId = spaceId };
        return Task.CompletedTask;
    }

    public Task<bool> SetStatusAsync(long id, string status, string? from, CancellationToken cancellationToken)
    {
        var at = Rows.FindIndex(r => r.Id == id);
        if (at < 0 || (from is not null && Rows[at].Status != from))
        {
            return Task.FromResult(false);
        }

        Rows[at] = Rows[at] with { Status = status };
        return Task.FromResult(true);
    }

    public Task SetAnalysisAsync(long id, string analysis, Guid session, CancellationToken cancellationToken)
    {
        var at = Rows.FindIndex(r => r.Id == id);
        Rows[at] = Rows[at] with { Analysis = analysis, Session = session, Status = Rows[at].Status == IncidentStatus.Investigating ? IncidentStatus.Open : Rows[at].Status };
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetCursorAsync(string app, CancellationToken cancellationToken) =>
        Task.FromResult(Cursors.TryGetValue(app, out var at) ? at : (DateTimeOffset?)null);

    public Task SetCursorAsync(string app, DateTimeOffset checkedUntil, CancellationToken cancellationToken)
    {
        Cursors[app] = checkedUntil;
        return Task.CompletedTask;
    }
}
