using System.Globalization;
using Agentd.Application.Incidents;

namespace Agentd.Infrastructure.Monitoring;

/// <summary>
/// The KQL the monitor runs on an Application Insights component (resource-centric, so the classic tables
/// <c>exceptions</c> and <c>requests</c>), and how its rows become <see cref="ErrorGroup"/>s.
/// </summary>
internal static class TelemetryQueries
{
    /// <summary>The most frequent groups a pass looks at.</summary>
    public const int MaxGroups = 50;

    /// <summary>Exceptions by problem and operation, failed requests by name and result code; the latest occurrence is the sample.</summary>
    public static string Errors(string? roleName)
    {
        var role = string.IsNullOrWhiteSpace(roleName) ? "''" : "@'" + roleName.Replace("'", "''", StringComparison.Ordinal) + "'";
        return string.Create(CultureInfo.InvariantCulture, $$"""
            let roleFilter = {{role}};
            union
            (exceptions
             | where isempty(roleFilter) or cloud_RoleName == roleFilter
             | extend ProblemId = problemId, Operation = operation_Name, Message = outerMessage, Stack = tostring(details[0].rawStack),
                      Role = cloud_RoleName, OperationId = operation_Id, Type = type
             | summarize Count = count(), Users = dcount(user_Id), FirstSeen = min(timestamp), LastSeen = max(timestamp),
                         arg_max(timestamp, Message, Stack, Role, OperationId, Type) by ProblemId, Operation
             | extend Kind = 'exception', Fingerprint = strcat('ex|', ProblemId, '|', Operation), Title = Type, ResultCode = ''),
            (requests
             | where success == false
             | where isempty(roleFilter) or cloud_RoleName == roleFilter
             | extend Name = name, Code = resultCode, Role = cloud_RoleName, OperationId = operation_Id, Url = url
             | summarize Count = count(), Users = dcount(user_Id), FirstSeen = min(timestamp), LastSeen = max(timestamp),
                         arg_max(timestamp, Role, OperationId, Url) by Name, Code
             | extend Kind = 'request', Fingerprint = strcat('req|', Name, '|', Code), Title = strcat(Name, ' → ', Code),
                      Operation = Name, Message = Url, Stack = '', ResultCode = Code)
            | project Kind, Fingerprint, Title, Message, Operation, Role, Count, Users, FirstSeen, LastSeen, Stack, ResultCode, OperationId
            | order by Count desc
            | take {{MaxGroups}}
            """);
    }

    /// <summary>One result row (column name → value) as a group.</summary>
    public static ErrorGroup ToGroup(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var kind = Text(row, "Kind") == ErrorKinds.Request ? ErrorKinds.Request : ErrorKinds.Exception;
        var title = Text(row, "Title");
        return new ErrorGroup(
            Text(row, "Fingerprint") ?? throw new FormatException("A telemetry row has no Fingerprint."),
            kind,
            string.IsNullOrWhiteSpace(title) ? (kind == ErrorKinds.Request ? "Failed request" : "Exception") : title,
            Text(row, "Message"),
            Text(row, "Operation"),
            Text(row, "Role"),
            Number(row, "Count"),
            Number(row, "Users"),
            Time(row, "FirstSeen"),
            Time(row, "LastSeen"),
            new ErrorSample(Text(row, "Stack"), Text(row, "ResultCode"), Text(row, "OperationId")));
    }

    private static string? Text(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null && Convert.ToString(v, CultureInfo.InvariantCulture) is { Length: > 0 } s ? s : null;

    private static long Number(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;

    private static DateTimeOffset Time(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) ? v switch
        {
            DateTimeOffset at => at,
            DateTime at => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)),
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            _ => throw new FormatException($"A telemetry row's {column} isn't a time."),
        } : throw new FormatException($"A telemetry row has no {column}.");
}
