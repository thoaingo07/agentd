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

    /// <summary>The most occurrences an investigation reads.</summary>
    public const int MaxOccurrences = 5;

    /// <summary>Exceptions by problem and operation, failed requests by name and result code; the latest occurrence is the sample.</summary>
    public static string Errors(string? roleName)
    {
        return string.Create(CultureInfo.InvariantCulture, $$"""
            let roleFilter = {{Literal(roleName)}};
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

    /// <summary>
    /// An incident's latest occurrences, whole: an exception's message and stack with its request's url and result code, or a
    /// failed request with the exception logged in the same operation. Matched by the incident's fingerprint.
    /// </summary>
    public static string Occurrences(Incident incident, string? roleName, int max)
    {
        ArgumentNullException.ThrowIfNull(incident);
        var (key, last) = Parts(incident.Fingerprint);
        var take = Math.Clamp(max, 1, MaxOccurrences);
        return incident.Kind == ErrorKinds.Request
            ? string.Create(CultureInfo.InvariantCulture, $$"""
                let roleFilter = {{Literal(roleName)}};
                requests
                | where success == false and name == {{Literal(key)}} and resultCode == {{Literal(last)}}
                | where isempty(roleFilter) or cloud_RoleName == roleFilter
                | top {{take}} by timestamp desc
                | project At = timestamp, Operation = name, OperationId = operation_Id, Url = url, ResultCode = resultCode, Properties = tostring(customDimensions)
                | join kind=leftouter (exceptions | summarize arg_max(timestamp, outerMessage, Raw = tostring(details[0].rawStack)) by operation_Id)
                    on $left.OperationId == $right.operation_Id
                | project At, Message = outerMessage, Stack = Raw, Operation, OperationId, Url, ResultCode, Properties
                | order by At desc
                """)
            : string.Create(CultureInfo.InvariantCulture, $$"""
                let roleFilter = {{Literal(roleName)}};
                exceptions
                | where problemId == {{Literal(key)}} and operation_Name == {{Literal(last)}}
                | where isempty(roleFilter) or cloud_RoleName == roleFilter
                | top {{take}} by timestamp desc
                | project At = timestamp, Message = iff(isnotempty(innermostMessage) and innermostMessage != outerMessage, strcat(outerMessage, ' → ', innermostMessage), outerMessage),
                          Stack = tostring(details[0].rawStack), Operation = operation_Name, OperationId = operation_Id, Properties = tostring(customDimensions)
                | join kind=leftouter (requests | project operation_Id, Url = url, ResultCode = resultCode) on $left.OperationId == $right.operation_Id
                | project At, Message, Stack, Operation, OperationId, Url, ResultCode, Properties
                | order by At desc
                """);
    }

    /// <summary>One occurrence row as an <see cref="ErrorOccurrence"/>.</summary>
    public static ErrorOccurrence ToOccurrence(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new ErrorOccurrence(Time(row, "At"), Text(row, "Message"), Text(row, "Stack"), Text(row, "Operation"), Text(row, "OperationId"), Text(row, "Url"),
            Text(row, "ResultCode"), Text(row, "Properties") is { } p && p != "{}" ? p : null);
    }

    /// <summary>A fingerprint's middle (problem id or request name) and last part (operation or result code).</summary>
    private static (string Key, string Last) Parts(string fingerprint)
    {
        var first = fingerprint.IndexOf('|', StringComparison.Ordinal);
        var last = fingerprint.LastIndexOf('|');
        return first < 0 || last <= first ? throw new FormatException($"'{fingerprint}' isn't an incident fingerprint.")
            : (fingerprint[(first + 1)..last], fingerprint[(last + 1)..]);
    }

    /// <summary>A KQL verbatim string literal.</summary>
    private static string Literal(string? value) =>
        string.IsNullOrEmpty(value) ? "''" : "@'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

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
