using System.Collections.Concurrent;
using System.Text.Json;
using Agentd.Application.Permissions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Agentd.Bff.Testing;

/// <summary>
/// <c>/__test/*</c>: seeding for the Playwright suite (T3.12). Mapped <b>only</b> in the <c>E2E</c> environment,
/// where the scheduler is off, so jobs and events come from here instead of agents.
/// </summary>
public static class TestSeedEndpoints
{
    public const string EnvironmentName = "E2E";

    public static bool ShouldMap(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsEnvironment(EnvironmentName);
    }

    public static void MapTestSeeding(this IEndpointRouteBuilder endpoints, IHostEnvironment environment)
    {
        if (!ShouldMap(environment))
        {
            return;
        }

        var test = endpoints.MapGroup("/__test").AllowAnonymous().ExcludeFromDescription();

        // A job in Running (or WaitingForHuman), as if an agent had started it.
        test.MapPost("/jobs", async (SeedJob body, [FromServices] IJobRepository jobs, [FromServices] IClock clock, CancellationToken ct) =>
        {
            var job = Job.Create(WorkItemId.From(body.WorkItemId), RepositoryName.From("sysmin"), body.Title, clock);
            await jobs.AddAsync(job, ct).ConfigureAwait(false);
            job.BeginPreparing();
            job.Start(new WorktreePath($"/tmp/agentd-e2e/wi-{body.WorkItemId}"), BranchName.From($"ai/{body.WorkItemId}-e2e"), ClaudeSessionId.New());
            if (body.Waiting)
            {
                job.AskDeveloper("v1 or v2?");
            }

            await jobs.SaveAsync(job, ct).ConfigureAwait(false);
            return TypedResults.Ok(new { id = job.Id.Value });
        });

        // Agent output: "e2e event <from>" … "e2e event <from + count - 1>".
        test.MapPost("/jobs/{id:long}/events", async (long id, SeedEvents body, [FromServices] IEventStore events, CancellationToken ct) =>
        {
            long last = 0;
            for (var i = 0; i < body.Count; i++)
            {
                last = await events.AppendAsync(new JobId(id), "agent.text", JsonSerializer.Serialize(new { text = $"e2e event {body.From + i}" }), ct).ConfigureAwait(false);
            }

            return TypedResults.Ok(new { lastSeq = last });
        });

        test.MapPost("/jobs/{id:long}/permissions", async (long id, [FromServices] IPermissionStore store, CancellationToken ct) =>
            TypedResults.Ok(new { id = await store.InsertAsync(new JobId(id), "Bash", "npm install --no-audit", ["Bash(npm install:*)"], ct).ConfigureAwait(false) }));

        test.MapGet("/csp-reports", ([FromServices] CspReportLog log) => TypedResults.Ok(log.Snapshot()));
    }

    public sealed record SeedJob(int WorkItemId, string Title, bool Waiting = false);

    public sealed record SeedEvents(int Count, int From = 1);
}

/// <summary>The latest CSP reports the browser sent (also logged), so the E2E suite can assert there were none.</summary>
public sealed class CspReportLog
{
    public const int Capacity = 100;

    private readonly ConcurrentQueue<CspReport> _reports = new();

    public void Add(CspReport report)
    {
        _reports.Enqueue(report);
        while (_reports.Count > Capacity && _reports.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<CspReport> Snapshot() => _reports.ToArray();
}

public sealed record CspReport(string Directive, string BlockedUrl, string DocumentUrl, string Disposition);
