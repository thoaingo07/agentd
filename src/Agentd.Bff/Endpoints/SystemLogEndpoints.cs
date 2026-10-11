using Agentd.Application.SystemLog;
using Agentd.Bff.Http;
using Agentd.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Agentd.Bff.Endpoints;

/// <summary><c>GET /api/system-log</c>: the daemon's own log, newest first (Settings → System log, Admins only).</summary>
public static class SystemLogEndpoints
{
    public const int MaxPage = 200;

    public static RouteGroupBuilder MapSystemLog(this RouteGroupBuilder api)
    {
        api.MapGet("/system-log", async Task<IResult> (long? before, string? level, string? category, string? q, int? limit, [FromServices] ISystemLogStore store, CancellationToken ct) =>
        {
            var min = LogLevel.Information;
            if (!string.IsNullOrWhiteSpace(level) && (!Enum.TryParse(level, ignoreCase: true, out min) || min is LogLevel.None || int.TryParse(level, out _)))
            {
                return DomainError.Validation("level is Trace, Debug, Information, Warning, Error or Critical.").ToProblem();
            }

            var size = Math.Clamp(limit ?? 100, 1, MaxPage);
            var rows = await store.ListAsync(before, min, Blank(category), Blank(q), size + 1, ct).ConfigureAwait(false);
            return TypedResults.Ok(new SystemLogPageVm(
                [.. rows.Take(size).Select(e => new SystemLogEntryVm(e.Id, e.At, e.Level.ToString(), e.Category, e.Message, e.Exception, e.JobId))],
                rows.Count > size));
        }).RequireAuthorization(p => p.RequireRole("Admin"))
            .WithName("GetSystemLog").Produces<SystemLogPageVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        return api;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One entry; <c>level</c> is Trace … Critical, <c>category</c> the logger's (e.g. Agentd.Application.Jobs.Scheduler).</summary>
public sealed record SystemLogEntryVm(long Id, DateTimeOffset At, string Level, string Category, string Message, string? Exception, long? JobId);

/// <summary>A page, newest first; <c>hasMore</c>: older entries exist (ask again with <c>before</c> = the last id).</summary>
public sealed record SystemLogPageVm(IReadOnlyList<SystemLogEntryVm> Entries, bool HasMore);
