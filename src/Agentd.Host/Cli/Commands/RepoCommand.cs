using System.CommandLine;
using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Application.Repositories;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;

namespace Agentd.Host.Cli.Commands;

internal static class RepoCommand
{
    public static Command Create(CliContext context) =>
        new("repo", "Register the repositories agentd works on.") { Add(context), List(context), Remove(context) };

    private static Command Add(CliContext context)
    {
        var url = new Argument<string>("url") { Description = "SSH or HTTPS clone URL, e.g. git@ssh.dev.azure.com:v3/org/project/repo." };
        var name = new Option<string?>("--name") { Description = "Name used in tags and paths (default: the repository name)." };
        var baseBranch = new Option<string?>("--base") { Description = "Base branch for pull requests (default: the remote's default branch)." };
        var tag = new Option<string?>("--tag") { Description = "Work item tag that selects this repository (default: repo:<name>)." };
        var areaPaths = new Option<string[]>("--area-path") { Description = "Area path prefix that selects this repository (repeatable)." };
        var command = new Command("add", "Register a repository by URL and clone it.") { url, name, baseBranch, tag, areaPaths };
        command.SetAction(async (parse, ct) =>
        {
            var scope = context.CreateScope();
            await using (scope.ConfigureAwait(false))
            {
                var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<AddRepository, Repository>>().Handle(
                    new AddRepository(parse.GetValue(url)!, parse.GetValue(name), parse.GetValue(baseBranch), parse.GetValue(tag), parse.GetValue(areaPaths) ?? []),
                    ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    await context.Error.WriteLineAsync(result.Error.Message).ConfigureAwait(false);
                    return ExitCodes.From(result.Error);
                }

                var repo = result.Value;
                await context.Out.WriteLineAsync($"Registered {repo.Name}: base branch {repo.BaseBranch}, matched by {Match(repo)}.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }
        });
        return command;
    }

    private static Command List(CliContext context)
    {
        var command = new Command("list", "List registered repositories.");
        command.SetAction(async (_, ct) =>
        {
            var repos = await context.Services.GetRequiredService<IRepositoryRegistry>().ListAsync(ct).ConfigureAwait(false);
            if (repos.Count == 0)
            {
                await context.Out.WriteLineAsync("No repositories registered. Add one with: agentd repo add <url>").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            var rows = repos.Select(r => new[] { r.Name.Value, r.BaseBranch, Match(r), r.RemoteUrl })
                .Prepend(["NAME", "BASE", "MATCH", "URL"]).ToList();
            await context.Out.WriteAsync(TextTable.Render(rows)).ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Remove(CliContext context)
    {
        var name = new Argument<string>("name") { Description = "The registered repository name." };
        var command = new Command("remove", "Unregister a repository (job history is kept).") { name };
        command.SetAction(async (parse, ct) =>
        {
            if (RepositoryName.Create(parse.GetValue(name)!) is not { IsSuccess: true } repoName)
            {
                await context.Error.WriteLineAsync("The repository name must not be empty.").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            var scope = context.CreateScope();
            await using (scope.ConfigureAwait(false))
            {
                var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<RemoveRepository, Application.Abstractions.Unit>>()
                    .Handle(new RemoveRepository(repoName.Value), ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    await context.Error.WriteLineAsync(result.Error.Message).ConfigureAwait(false);
                    return ExitCodes.From(result.Error);
                }

                await context.Out.WriteLineAsync($"Removed {repoName.Value}.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }
        });
        return command;
    }

    private static string Match(Repository repo) =>
        string.Join(", ", new[] { repo.MatchTag is null ? null : $"tag {repo.MatchTag}" }
            .Concat(repo.MatchAreaPaths.Select(p => $"area {p}"))
            .OfType<string>()
            .DefaultIfEmpty("nothing (use agentd run --repo)"));
}
