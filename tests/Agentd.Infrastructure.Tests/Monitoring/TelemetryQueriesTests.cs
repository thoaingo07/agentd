using Agentd.Application.Incidents;
using Agentd.Infrastructure.Monitoring;

namespace Agentd.Infrastructure.Tests.Monitoring;

[TestClass]
public sealed class TelemetryQueriesTests
{
    private static readonly DateTime s_first = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void An_exception_row_becomes_a_group_with_its_sample()
    {
        var group = TelemetryQueries.ToGroup(new Dictionary<string, object?>
        {
            ["Kind"] = "exception",
            ["Fingerprint"] = "ex|123|GET /orders",
            ["Title"] = "System.NullReferenceException",
            ["Message"] = "Object reference not set",
            ["Operation"] = "GET /orders",
            ["Role"] = "orders-api",
            ["Count"] = 42L,
            ["Users"] = 7L,
            ["FirstSeen"] = s_first,
            ["LastSeen"] = new DateTimeOffset(2026, 10, 14, 9, 14, 0, TimeSpan.Zero),
            ["Stack"] = "at Orders.Get()",
            ["ResultCode"] = "",
            ["OperationId"] = "abc",
        });

        Assert.AreEqual(new ErrorGroup("ex|123|GET /orders", ErrorKinds.Exception, "System.NullReferenceException", "Object reference not set", "GET /orders", "orders-api",
            42, 7, new DateTimeOffset(s_first), new DateTimeOffset(2026, 10, 14, 9, 14, 0, TimeSpan.Zero), new ErrorSample("at Orders.Get()", null, "abc")), group);
    }

    [TestMethod]
    public void A_failed_request_row_without_a_title_gets_one_and_counts_of_any_number_type()
    {
        var group = TelemetryQueries.ToGroup(new Dictionary<string, object?>
        {
            ["Kind"] = "request",
            ["Fingerprint"] = "req|GET /x|500",
            ["Title"] = null,
            ["Count"] = 3,
            ["Users"] = 0.0,
            ["FirstSeen"] = "2026-10-14T09:00:00Z",
            ["LastSeen"] = "2026-10-14T09:05:00Z",
            ["ResultCode"] = "500",
        });

        Assert.AreEqual((ErrorKinds.Request, "Failed request", 3L, 0L, "500"), (group.Kind, group.Title, group.Count, group.Users, group.Sample.ResultCode));
        Assert.AreEqual(new DateTimeOffset(s_first), group.FirstSeen);
        Assert.ThrowsExactly<FormatException>(() => TelemetryQueries.ToGroup(new Dictionary<string, object?> { ["FirstSeen"] = s_first, ["LastSeen"] = s_first }));
    }

    [TestMethod]
    public void The_query_filters_on_the_role_as_a_quoted_literal()
    {
        StringAssert.Contains(TelemetryQueries.Errors(null), "let roleFilter = '';");
        StringAssert.Contains(TelemetryQueries.Errors("orders-api"), "let roleFilter = @'orders-api';");
        StringAssert.Contains(TelemetryQueries.Errors("o'x"), "let roleFilter = @'o''x';");
        StringAssert.Contains(TelemetryQueries.Errors(null), $"take {TelemetryQueries.MaxGroups}");
        StringAssert.Contains(TelemetryQueries.Errors(null), "strcat('ex|', ProblemId, '|', Operation)");
    }

    [TestMethod]
    public void An_incidents_occurrences_are_matched_by_its_fingerprint_with_the_operation_joined()
    {
        var exception = TelemetryQueries.Occurrences(Incident(ErrorKinds.Exception, "ex|12|34|GET /o'rders"), "orders-api", 99);
        StringAssert.Contains(exception, "where problemId == @'12|34' and operation_Name == @'GET /o''rders'", "the problem id keeps its own bars");
        StringAssert.Contains(exception, $"top {TelemetryQueries.MaxOccurrences} by timestamp desc", "capped");
        StringAssert.Contains(exception, "join kind=leftouter (requests");
        StringAssert.Contains(exception, "let roleFilter = @'orders-api';");

        var request = TelemetryQueries.Occurrences(Incident(ErrorKinds.Request, "req|GET /orders|500"), null, 3);
        StringAssert.Contains(request, "where success == false and name == @'GET /orders' and resultCode == @'500'");
        StringAssert.Contains(request, "top 3 by timestamp desc");
        StringAssert.Contains(request, "join kind=leftouter (exceptions");
        Assert.ThrowsExactly<FormatException>(() => TelemetryQueries.Occurrences(Incident(ErrorKinds.Request, "nobars"), null, 3));
    }

    [TestMethod]
    public void An_occurrence_row_keeps_its_stack_request_and_custom_dimensions()
    {
        var o = TelemetryQueries.ToOccurrence(new Dictionary<string, object?>
        {
            ["At"] = s_first,
            ["Message"] = "boom → inner",
            ["Stack"] = "at Orders.Get()",
            ["Operation"] = "GET /orders",
            ["OperationId"] = "abc",
            ["Url"] = "https://o/api/orders",
            ["ResultCode"] = "500",
            ["Properties"] = "{\"tenant\":\"acme\"}",
        });
        var bare = TelemetryQueries.ToOccurrence(new Dictionary<string, object?> { ["At"] = s_first, ["Properties"] = "{}", ["Url"] = "" });

        Assert.AreEqual(new ErrorOccurrence(new DateTimeOffset(s_first), "boom → inner", "at Orders.Get()", "GET /orders", "abc", "https://o/api/orders", "500", "{\"tenant\":\"acme\"}"), o);
        Assert.AreEqual((null, null), (bare.Properties, bare.Url), "empty dimensions and blanks are nothing");
    }

    private static Incident Incident(string kind, string fingerprint) =>
        new(1, "orders-api", fingerprint, kind, "t", null, null, new ErrorSample(null, null, null), IncidentStatus.Open, 1, s_first, s_first, null, null, null, null, s_first);
}
