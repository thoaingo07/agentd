using System.CommandLine;
using System.Globalization;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;

namespace Agentd.Host.Cli.Commands;

internal static class StatusCommand
{
    public static Command Create(CliContext context)
    {
        var all = new Option<bool>("--all") { Description = "Include finished jobs from the last 24 hours." };
        var command = new Command("status", "List jobs.") { all };
        command.SetAction(async (parse, ct) =>
        {
            var includeRecent = parse.GetValue(all);
            using (var daemon = Control.DaemonClient.TryCreate(context.Home))
            {
                if (daemon is not null && await daemon.StatusAsync(includeRecent, ct).ConfigureAwait(false) is { } live)
                {
                    await context.Out.WriteAsync(RenderLive(live, includeRecent)).ConfigureAwait(false);
                    return ExitCodes.Ok;
                }
            }

            await context.Error.WriteLineAsync("(daemon not running; from the database)").ConfigureAwait(false);
            var scope = context.CreateScope();
            await using (scope.ConfigureAwait(false))
            {
                var rows = await scope.ServiceProvider.GetRequiredService<IQueryHandler<GetJobStatus, IReadOnlyList<JobStatusRow>>>()
                    .Handle(new GetJobStatus(includeRecent), ct).ConfigureAwait(false);
                await context.Out.WriteAsync(Render(rows, includeRecent)).ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    internal static string Render(IReadOnlyList<JobStatusRow> rows, bool includeRecent)
    {
        if (rows.Count == 0)
        {
            return includeRecent ? "No jobs in the last 24 hours.\n" : "No active jobs.\n";
        }

        string[] header = ["ID", "WORK ITEM", "REPO", "STATE", "ELAPSED", "ATTEMPT", "PR / LAST ERROR"];
        var table = rows.Select(r => new[]
        {
            r.Id.ToString(CultureInfo.InvariantCulture),
            "#" + r.WorkItemId.ToString(CultureInfo.InvariantCulture),
            r.Repository,
            r.State.ToString(),
            Elapsed(r.Elapsed),
            r.Attempt.ToString(CultureInfo.InvariantCulture),
            r.PullRequestUrl ?? OneLine(r.LastError),
        }).Prepend(header).ToList();
        return TextTable.Render(table);
    }

    /// <summary>The daemon's answer: the same columns plus what each job is doing right now.</summary>
    internal static string RenderLive(IReadOnlyList<Control.ControlJob> jobs, bool includeRecent)
    {
        if (jobs.Count == 0)
        {
            return includeRecent ? "No jobs in the last 24 hours.\n" : "No active jobs.\n";
        }

        string[] header = ["ID", "WORK ITEM", "REPO", "STATE", "PHASE", "ELAPSED", "ACTIVITY", "PR / LAST ERROR"];
        return TextTable.Render([.. jobs.Select(j => new[]
        {
            j.Id.ToString(CultureInfo.InvariantCulture),
            "#" + j.WorkItemId.ToString(CultureInfo.InvariantCulture),
            j.Repository,
            j.State,
            j.Phase ?? "",
            Elapsed(TimeSpan.FromSeconds(j.ElapsedSeconds)),
            OneLine(j.Activity),
            j.PullRequestUrl ?? OneLine(j.LastError),
        }).Prepend(header)]);
    }

    internal static string Elapsed(TimeSpan t) => t switch
    {
        { TotalMinutes: < 1 } => $"{Math.Max(0, (int)t.TotalSeconds)}s",
        { TotalHours: < 1 } => $"{(int)t.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)t.TotalHours}h{t.Minutes:00}m",
        _ => $"{(int)t.TotalDays}d{t.Hours:00}h",
    };

    private static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..77] + "...";
    }
}
