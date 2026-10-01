using System.CommandLine;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Host.Cli.Commands;

internal static class RunCommand
{
    public static Command Create(CliContext context)
    {
        var workItem = new Argument<int>("workItemId") { Description = "The Azure DevOps work item ID." };
        var repo = new Option<string?>("--repo") { Description = "Use this registered repository instead of matching by tag or area path." };
        var command = new Command("run", "Queue a job for a work item now, even without the ai-workflow tag.") { workItem, repo };
        command.SetAction(async (parse, ct) =>
        {
            if (WorkItemId.Create(parse.GetValue(workItem)) is not { IsSuccess: true } id)
            {
                await context.Error.WriteLineAsync("The work item ID must be a positive number.").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            RepositoryName? repository = null;
            if (parse.GetValue(repo) is { } name)
            {
                if (RepositoryName.Create(name) is not { IsSuccess: true } parsed)
                {
                    await context.Error.WriteLineAsync("--repo must not be empty.").ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                repository = parsed.Value;
            }

            var scope = context.CreateScope();
            await using (scope.ConfigureAwait(false))
            {
                var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<ClaimWorkItem, JobId>>()
                    .Handle(new ClaimWorkItem(id.Value, Force: true, repository), ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    await context.Error.WriteLineAsync(result.Error.Message).ConfigureAwait(false);
                    // An ambiguous repository match is a "no repo match" for the operator: pass --repo.
                    return result.Error.Code == "validation" ? ExitCodes.NotFound : ExitCodes.From(result.Error);
                }

                await context.Out.WriteLineAsync($"Queued job #{result.Value} for work item #{id.Value}. The daemon starts it when a slot is free.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }
        });
        return command;
    }
}
