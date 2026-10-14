using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Incidents;

/// <summary>
/// Configuration section <c>Agentd:Monitor</c>: the incident monitor (docs/architect/incident-monitor.md) reads the listed
/// Application Insights resources for exceptions and failed requests, and alerts on new ones.
/// </summary>
public sealed class IncidentMonitorOptions
{
    public const string Section = "Agentd:Monitor";

    public bool Enabled { get; set; }

    /// <summary>How often each app is read.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How far back the first pass looks; later passes start where the last one ended.</summary>
    public TimeSpan FirstWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>An error group becomes an incident with at least this many occurrences in a pass (a known one always counts).</summary>
    public int MinCount { get; set; } = 3;

    /// <summary>At most this many alerts per pass; the rest are recorded quietly.</summary>
    public int MaxNewPerPass { get; set; } = 5;

    /// <summary>The Entra directory to sign in to, when it isn't the default one.</summary>
    public string? TenantId { get; set; }

    public IList<MonitoredApp> Apps { get; } = [];
}

/// <summary>An Application Insights resource to watch, and the registered repository its code is in.</summary>
public sealed class MonitoredApp
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The Application Insights component's ARM id: <c>/subscriptions/…/resourceGroups/…/providers/Microsoft.Insights/components/…</c>.</summary>
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>The registered repository the app's code is in (investigations and fixes run there).</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Only telemetry with this <c>cloud_RoleName</c>, when several apps share one resource.</summary>
    public string? RoleName { get; set; }
}

/// <summary>Fails startup on a monitor configuration that cannot work.</summary>
public sealed partial class IncidentMonitorOptionsValidator : IValidateOptions<IncidentMonitorOptions>
{
    public ValidateOptionsResult Validate(string? name, IncidentMonitorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        const string at = IncidentMonitorOptions.Section;
        if (options.Apps.Count == 0)
        {
            errors.Add($"{at}:Apps: list at least one Application Insights resource, or turn the monitor off.");
        }

        if (options.PollInterval < TimeSpan.FromMinutes(1))
        {
            errors.Add($"{at}:PollInterval: at least 00:01:00.");
        }

        if (options.FirstWindow <= TimeSpan.Zero || options.FirstWindow > TimeSpan.FromDays(1))
        {
            errors.Add($"{at}:FirstWindow: more than zero and at most a day.");
        }

        if (options.MinCount < 1 || options.MaxNewPerPass < 1)
        {
            errors.Add($"{at}: MinCount and MaxNewPerPass are at least 1.");
        }

        for (var i = 0; i < options.Apps.Count; i++)
        {
            var app = options.Apps[i];
            if (!AppName().IsMatch(app.Name))
            {
                errors.Add($"{at}:Apps:{i}:Name: letters, digits, '.', '_' and '-', at most 64.");
            }
            else if (options.Apps.Take(i).Any(a => string.Equals(a.Name, app.Name, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{at}:Apps:{i}:Name: '{app.Name}' is listed twice.");
            }

            if (!ComponentId().IsMatch(app.ResourceId))
            {
                errors.Add($"{at}:Apps:{i}:ResourceId: an Application Insights component's id (/subscriptions/…/resourceGroups/…/providers/Microsoft.Insights/components/…).");
            }

            if (string.IsNullOrWhiteSpace(app.Repository))
            {
                errors.Add($"{at}:Apps:{i}:Repository: the registered repository the app's code is in.");
            }

            if (app.RoleName is { Length: > 0 } role && !RoleName().IsMatch(role))
            {
                errors.Add($"{at}:Apps:{i}:RoleName: letters, digits, spaces and '.', '_', '-', '/', ':', at most 128.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex AppName();

    [GeneratedRegex(@"^/subscriptions/[^/\s]+/resourceGroups/[^/\s]+/providers/Microsoft\.Insights/components/[^/\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ComponentId();

    [GeneratedRegex(@"^[A-Za-z0-9 ._:/-]{1,128}$")]
    private static partial Regex RoleName();
}

/// <summary>What an error group is: an exception, or a failed request.</summary>
public static class ErrorKinds
{
    public const string Exception = "exception";
    public const string Request = "request";
}

/// <summary>Where an incident stands.</summary>
public static class IncidentStatus
{
    public const string Open = "Open";
    public const string Investigating = "Investigating";
    public const string Fixing = "Fixing";
    public const string Resolved = "Resolved";
    public const string Ignored = "Ignored";
}

/// <summary>One occurrence's details, to show and investigate: the top of the stack, the result code, the operation.</summary>
public sealed record ErrorSample(string? Stack, string? ResultCode, string? OperationId);

/// <summary>
/// Telemetry errors grouped by <see cref="Fingerprint"/> over a window: one bug is one group however often it happens.
/// Exceptions are <c>ex|{problemId}|{operation}</c>, failed requests <c>req|{name}|{resultCode}</c>.
/// </summary>
public sealed record ErrorGroup(string Fingerprint, string Kind, string Title, string? Message, string? Operation, string? RoleName, long Count, long Users,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, ErrorSample Sample);

/// <summary>A recorded error group, with its alert thread, the investigating agent's analysis and session, and the Bug filed for it.</summary>
public sealed record Incident(long Id, string App, string Fingerprint, string Kind, string Title, string? Message, string? Operation, ErrorSample Sample,
    string Status, long TotalCount, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string? Provider, string? ThreadId, string? SpaceId, int? WorkItemId,
    DateTimeOffset CreatedAt, string? Analysis = null, Guid? Session = null, DateTimeOffset? AnalyzedAt = null);

/// <summary>One occurrence of an incident, whole, for the investigating agent: when, the message, the full stack, the request and its custom dimensions.</summary>
public sealed record ErrorOccurrence(DateTimeOffset At, string? Message, string? Stack, string? Operation, string? OperationId, string? Url, string? ResultCode,
    string? Properties);

/// <summary>What recording a group did: a new incident, a resolved one back, or a known one counted again.</summary>
public sealed record IncidentRecorded(long Id, bool Created, bool Reopened);

/// <summary>Reads an app's telemetry (Application Insights, in Infrastructure.Monitoring).</summary>
public interface ITelemetrySource
{
    /// <summary>The exceptions and failed requests in [<paramref name="since"/>, <paramref name="until"/>), grouped, most frequent first.</summary>
    Task<IReadOnlyList<ErrorGroup>> ErrorsAsync(MonitoredApp app, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>The incident's latest occurrences since <paramref name="since"/> (at most <paramref name="max"/>), newest first.</summary>
    Task<IReadOnlyList<ErrorOccurrence>> OccurrencesAsync(MonitoredApp app, Incident incident, DateTimeOffset since, int max, CancellationToken cancellationToken);
}

/// <summary>Incidents and each app's read position, via <c>agentd.incident_*</c> routines.</summary>
public interface IIncidentStore
{
    /// <summary>
    /// Counts a group: a known incident always (a resolved one reopens), a new one only with at least <paramref name="minCount"/>
    /// occurrences; null when it isn't an incident.
    /// </summary>
    Task<IncidentRecorded?> RecordAsync(string app, ErrorGroup group, int minCount, CancellationToken cancellationToken);

    Task<Incident?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Newest first; <paramref name="status"/> null is any.</summary>
    Task<IReadOnlyList<Incident>> ListAsync(string? status, int limit, CancellationToken cancellationToken);

    Task SetThreadAsync(long id, string provider, string threadId, string? spaceId, CancellationToken cancellationToken);

    /// <summary>Moves the incident to <paramref name="status"/>, only from <paramref name="from"/> when given; false when it wasn't there.</summary>
    Task<bool> SetStatusAsync(long id, string status, string? from, CancellationToken cancellationToken);

    /// <summary>Keeps the investigation's analysis and session; an incident still Investigating is Open again.</summary>
    Task SetAnalysisAsync(long id, string analysis, Guid session, CancellationToken cancellationToken);

    /// <summary>Where the app's last pass ended; null before the first.</summary>
    Task<DateTimeOffset?> GetCursorAsync(string app, CancellationToken cancellationToken);

    /// <summary>Moves the app's read position forward (never back).</summary>
    Task SetCursorAsync(string app, DateTimeOffset checkedUntil, CancellationToken cancellationToken);
}
