using Agentd.Application.Events;
using Agentd.Application.Setup;
using Agentd.Domain.Common;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Incidents;

/// <summary>
/// Settings → Monitoring: whether the incident monitor runs, the directory to sign in to, and the apps it reads.
/// <c>EnabledOverriddenBy</c> names what supplies <c>Monitor:Enabled</c> over agentd.json (saving it here then changes nothing).
/// </summary>
public sealed record MonitorStep(bool Enabled, string? TenantId, IReadOnlyList<MonitoredApp> Apps, string? EnabledOverriddenBy);

/// <summary>What Settings saves: the whole list of apps, in order.</summary>
public sealed record MonitorInput(bool Enabled, string? TenantId, IReadOnlyList<MonitoredApp> Apps);

/// <summary>
/// Settings → Monitoring (docs/architect/incident-monitor.md §4.2): the <c>Agentd:Monitor</c> section in agentd.json, used
/// after a restart, and <b>Test connection</b>, which reads an app's errors over the last hour with the running sign-in.
/// </summary>
public sealed class MonitorSettings(IConfigWriter config, ISettingsAudit audit, ITelemetrySource telemetry, TimeProvider time)
{
    public const string Section = "Monitor";

    public MonitorStep Get()
    {
        var apps = new List<MonitoredApp>();
        for (var i = 0; config.Read($"{Section}:Apps:{i}:Name") is { } name; i++)
        {
            apps.Add(new MonitoredApp
            {
                Name = name,
                ResourceId = config.Read($"{Section}:Apps:{i}:ResourceId") ?? string.Empty,
                Repository = config.Read($"{Section}:Apps:{i}:Repository") ?? string.Empty,
                RoleName = Blank(config.Read($"{Section}:Apps:{i}:RoleName")),
            });
        }

        return new MonitorStep(bool.TryParse(config.Read($"{Section}:Enabled"), out var on) && on, Blank(config.Read($"{Section}:TenantId")), apps,
            config.OverriddenBy($"{Section}:Enabled"));
    }

    /// <summary>Writes the section (validated like startup does), drops the apps after the list, and audits what changed.</summary>
    public async Task<Result<SaveResult>> SaveAsync(MonitorInput input, string by, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var apps = input.Apps.Select(Trim).ToList();
        if (Invalid(new MonitorInput(input.Enabled, input.TenantId, apps)) is { } problem)
        {
            return DomainError.Validation(problem);
        }

        if (config.SetBelow($"{Section}:Apps:{apps.Count}:Name") is { } below)
        {
            return DomainError.Validation($"agentd.json can't shorten the app list: {Section}:Apps:{apps.Count} would still come from {below}. Edit Agentd:Monitor:Apps there.");
        }

        var before = Get();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{Section}:Enabled"] = input.Enabled ? "true" : "false",
            [$"{Section}:TenantId"] = Blank(input.TenantId),
        };
        for (var i = 0; i < apps.Count; i++)
        {
            values[$"{Section}:Apps:{i}:Name"] = apps[i].Name;
            values[$"{Section}:Apps:{i}:ResourceId"] = apps[i].ResourceId;
            values[$"{Section}:Apps:{i}:Repository"] = apps[i].Repository;
            values[$"{Section}:Apps:{i}:RoleName"] = apps[i].RoleName;
        }

        for (var i = before.Apps.Count - 1; i >= apps.Count; i--)   // from the end, so each removal leaves the ones before it in place
        {
            values[$"{Section}:Apps:{i}"] = null;
        }

        var changed = new List<string>();
        if (before.Enabled != input.Enabled)
        {
            changed.Add($"{Section}:Enabled");
        }

        if (!string.Equals(before.TenantId, Blank(input.TenantId), StringComparison.Ordinal))
        {
            changed.Add($"{Section}:TenantId");
        }

        for (var i = 0; i < Math.Max(before.Apps.Count, apps.Count); i++)
        {
            if (i >= apps.Count || i >= before.Apps.Count || !Same(before.Apps[i], apps[i]))
            {
                changed.Add($"{Section}:Apps:{i}");
            }
        }

        var changes = changed.Select(k => new SettingsChange(k, "setting", values.TryGetValue(k, out var v) && v is null ? "removed" : "set", by, time.GetUtcNow())).ToList();
        if (changes.Count == 0)
        {
            return new SaveResult(RestartRequired: false);
        }

        await config.SetAsync(values, cancellationToken).ConfigureAwait(false);
        await audit.RecordAsync(changes, cancellationToken).ConfigureAwait(false);
        return new SaveResult(RestartRequired: true);
    }

    /// <summary>Reads the app's errors over the last hour, as the monitor would (the sign-in agentd started with). Nothing is saved.</summary>
    public async Task<StepCheck> TestAsync(MonitoredApp app, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        app = Trim(app);
        if (Invalid(new MonitorInput(true, null, [app])) is { } problem)
        {
            return new StepCheck(false, problem);
        }

        var now = time.GetUtcNow();
        try
        {
            var groups = await telemetry.ErrorsAsync(app, now.AddHours(-1), now, cancellationToken).ConfigureAwait(false);
            return new StepCheck(true, groups.Count == 0
                ? $"Read {app.Name}: no exceptions or failed requests in the last hour."
                : $"Read {app.Name}: {groups.Count} error group(s), {groups.Sum(g => g.Count)} occurrence(s) in the last hour.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StepCheck(false, $"Application Insights refused the query: {SecretRedactor.Redact(ex.Message)}",
                "Give agentd's identity (a managed identity, AZURE_* service principal, or this machine's az login) Reader or Monitoring Reader on the resource. " +
                "A new Directory (tenant) id is used after a restart.");
        }
    }

    /// <summary>What startup would refuse in this section (<see cref="IncidentMonitorOptionsValidator"/>), or null; an app list is checked even while off.</summary>
    private static string? Invalid(MonitorInput input)
    {
        var options = new IncidentMonitorOptions { Enabled = true, TenantId = Blank(input.TenantId) };
        foreach (var app in input.Apps)
        {
            options.Apps.Add(app);
        }

        if (!input.Enabled && options.Apps.Count == 0)
        {
            return null;
        }

        return new IncidentMonitorOptionsValidator().Validate(Options.DefaultName, options) is { Failed: true } failed
            ? string.Join(" ", failed.Failures ?? [])
            : null;
    }

    private static bool Same(MonitoredApp a, MonitoredApp b) =>
        (a.Name, a.ResourceId, a.Repository, a.RoleName) == (b.Name, b.ResourceId, b.Repository, b.RoleName);

    private static MonitoredApp Trim(MonitoredApp app) => new()
    {
        Name = app.Name?.Trim() ?? string.Empty,
        ResourceId = app.ResourceId?.Trim() ?? string.Empty,
        Repository = app.Repository?.Trim() ?? string.Empty,
        RoleName = Blank(app.RoleName),
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
