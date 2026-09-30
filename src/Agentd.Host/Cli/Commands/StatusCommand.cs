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
