using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Host.Control;

/// <summary>A job as <c>agentd status</c> shows it, with the daemon's live state (phase, last activity, resources).</summary>
internal sealed record ControlJob(
    long Id, int WorkItemId, string Repository, string State, double ElapsedSeconds, int Attempt, string? PullRequestUrl, string? LastError,
    string? Phase, string? Activity, string? Resources);

internal sealed record ControlRun(long JobId);

internal sealed record ControlError(string Code, string Message);

internal sealed record ControlHealth(string Status, string Version);

/// <summary><c>/control/*</c>, mapped only on the control socket's own server (see <see cref="ControlSocket"/>).</summary>
internal static class ControlEndpoints
{
    public static void Map(WebApplication server, IServiceProvider services)
    {
        server.MapGet("/control/health", () => Results.Ok(new ControlHealth("ok", Cli.Commands.UpdateCommand.BuiltVersion)));

        server.MapGet("/control/status", async (bool? all, CancellationToken ct) =>
        {
            var scope = services.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var rows = await scope.ServiceProvider.GetRequiredService<IQueryHandler<GetJobStatus, IReadOnlyList<JobStatusRow>>>()
                    .Handle(new GetJobStatus(all ?? false), ct).ConfigureAwait(false);
                var activity = services.GetRequiredService<JobActivity>();
                var now = services.GetRequiredService<Domain.Common.IClock>().UtcNow;
                return Results.Ok(rows.Select(r => Live(r, activity.Get(new JobId(r.Id)), now)).ToList());
            }
        });

        server.MapPost("/control/run/{id:int}", async (int id, string? repo, CancellationToken ct) =>
        {
            if (WorkItemId.Create(id) is not { IsSuccess: true } workItem)
            {
                return Results.BadRequest(new ControlError("validation", "The work item ID must be a positive number."));
            }

            RepositoryName? repository = null;
            if (repo is not null)
            {
                if (RepositoryName.Create(repo) is not { IsSuccess: true } name)
                {
                    return Results.BadRequest(new ControlError("validation", "--repo must not be empty."));
                }

                repository = name.Value;
            }

            var scope = services.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<ClaimWorkItem, JobId>>()
                    .Handle(new ClaimWorkItem(workItem.Value, Force: true, repository), ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return Results.Json(new ControlError(result.Error.Code, result.Error.Message), statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                services.GetService<JobDispatcher>()?.Signal();   // start it now, not at the next poll
                return Results.Ok(new ControlRun(result.Value.Value));
            }
        });
    }

    private static ControlJob Live(JobStatusRow r, ActivitySnapshot a, DateTimeOffset now) => new(
        r.Id, r.WorkItemId, r.Repository, r.State.ToString(), r.Elapsed.TotalSeconds, r.Attempt, r.PullRequestUrl, r.LastError,
        a.Phase,
        a.LastActivity is null ? null : $"{a.LastActivity}{(a.Running ? " (running)" : string.Empty)}{(a.LastActivityAt is { } at ? $", {JobActivity.Ago(now - at)} ago" : string.Empty)}",
        a.Resources?.Describe());
}
