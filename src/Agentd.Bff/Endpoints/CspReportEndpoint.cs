using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Agentd.Bff.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// <c>POST /api/csp-report</c>: browsers report CSP violations here (anonymous, no antiforgery header:
/// browsers can't add one). Body capped at 8 KB, rate-limited per IP, one structured warning per report.
/// </summary>
public static partial class CspReportEndpoint
{
    public const int MaxBodyBytes = 8 * 1024;
    public const string RateLimitPolicy = "csp-report";
    public const int PermitsPerMinute = 30;

    public static IServiceCollection AddCspReportRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

    public static IEndpointConventionBuilder MapCspReport(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(SecurityHeaders.ReportPath, async (HttpContext http, ILoggerFactory loggers) =>
        {
            if (http.Request.ContentLength > MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var buffer = new byte[MaxBodyBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await http.Request.Body.ReadAsync(buffer.AsMemory(read), http.RequestAborted).ConfigureAwait(false)) > 0)
            {
                read += n;
            }

            if (read > MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var logger = loggers.CreateLogger(typeof(CspReportEndpoint).FullName!);
            foreach (var report in Parse(Encoding.UTF8.GetString(buffer, 0, read)))
            {
                LogViolation(logger, report.Directive, report.BlockedUrl, report.DocumentUrl, report.Disposition);
            }

            return Results.NoContent();
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicy)
        .ExcludeFromDescription();

    internal sealed record Violation(string Directive, string BlockedUrl, string DocumentUrl, string Disposition);

    /// <summary>Reporting API (<c>application/reports+json</c>: an array of <c>{ type, body }</c>) or the legacy <c>{ "csp-report": … }</c>.</summary>
    internal static IReadOnlyList<Violation> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return root.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("body", out _))
                    .Select(r => r.GetProperty("body"))
                    .Select(b => new Violation(Text(b, "effectiveDirective"), Text(b, "blockedURL"), Text(b, "documentURL"), Text(b, "disposition")))
                    .ToList();
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("csp-report", out var legacy))
            {
                var directive = Text(legacy, "effective-directive");
                return [new Violation(directive.Length > 0 ? directive : Text(legacy, "violated-directive"), Text(legacy, "blocked-uri"), Text(legacy, "document-uri"), Text(legacy, "disposition"))];
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Truncate(v.GetString()!) : string.Empty;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];

    [LoggerMessage(Level = LogLevel.Warning, Message = "CSP violation: {Directive} blocked {BlockedUrl} on {DocumentUrl} ({Disposition})")]
    private static partial void LogViolation(ILogger logger, string directive, string blockedUrl, string documentUrl, string disposition);
}
