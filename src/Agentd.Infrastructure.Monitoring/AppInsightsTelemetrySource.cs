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
        var response = await client.QueryResourceAsync(new ResourceIdentifier(app.ResourceId), TelemetryQueries.Errors(app.RoleName),
            new LogsQueryTimeRange(since, until), new LogsQueryOptions { ServerTimeout = TimeSpan.FromMinutes(1) }, cancellationToken).ConfigureAwait(false);
        var result = response.Value;
        if (result.Status == LogsQueryResultStatus.Failure)
        {
            throw new InvalidOperationException($"The telemetry query for {app.Name} failed: {result.Error}");
        }

        var table = result.Table;
        var columns = table.Columns.Select(c => c.Name).ToList();
        return [.. table.Rows.Select(row => TelemetryQueries.ToGroup(columns.Select((name, i) => (name, value: row[i])).ToDictionary(c => c.name, c => (object?)c.value)))];
    }
}
