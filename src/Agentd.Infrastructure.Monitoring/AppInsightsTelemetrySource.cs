using Agentd.Application.Incidents;
using Azure.Core;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;

namespace Agentd.Infrastructure.Monitoring;

/// <summary><see cref="ITelemetrySource"/> over Azure Monitor Logs: a resource-centric query on the app's Application Insights component.</summary>
public sealed class AppInsightsTelemetrySource(LogsQueryClient client) : ITelemetrySource
{
    public async Task<IReadOnlyList<ErrorGroup>> ErrorsAsync(MonitoredApp app, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        return [.. (await QueryAsync(app, TelemetryQueries.Errors(app.RoleName), new LogsQueryTimeRange(since, until), cancellationToken).ConfigureAwait(false))
            .Select(TelemetryQueries.ToGroup)];
    }

    public async Task<IReadOnlyList<ErrorOccurrence>> OccurrencesAsync(MonitoredApp app, Incident incident, DateTimeOffset since, int max, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        return [.. (await QueryAsync(app, TelemetryQueries.Occurrences(incident, app.RoleName, max), new LogsQueryTimeRange(since, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false)).Select(TelemetryQueries.ToOccurrence)];
    }

    /// <summary>The query's rows, column name → value.</summary>
    private async Task<IEnumerable<Dictionary<string, object?>>> QueryAsync(MonitoredApp app, string query, LogsQueryTimeRange range, CancellationToken ct)
    {
        var response = await client.QueryResourceAsync(new ResourceIdentifier(app.ResourceId), query, range,
            new LogsQueryOptions { ServerTimeout = TimeSpan.FromMinutes(1) }, ct).ConfigureAwait(false);
        var result = response.Value;
        if (result.Status == LogsQueryResultStatus.Failure)
        {
            throw new InvalidOperationException($"The telemetry query for {app.Name} failed: {result.Error}");
        }

        var table = result.Table;
        var columns = table.Columns.Select(c => c.Name).ToList();
        return table.Rows.Select(row => columns.Select((name, i) => (name, value: row[i])).ToDictionary(c => c.name, c => (object?)c.value));
    }
}
