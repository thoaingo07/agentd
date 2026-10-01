using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Repositories;

/// <summary>Register any repository by URL (<c>agentd repo add</c>). The base branch is detected when not given.</summary>
public sealed record AddRepository(
    string Url,
    string? Name = null,
    string? BaseBranch = null,
    string? MatchTag = null,
    IReadOnlyList<string>? MatchAreaPaths = null);

public sealed class AddRepositoryHandler(IRepositoryRegistry registry, IGitRemote remote, IWorktreeManager worktrees)
    : ICommandHandler<AddRepository, Repository>
{
    public async Task<Result<Repository>> Handle(AddRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var url = RemoteUrl.Parse(command.Url);
        if (!url.IsSuccess)
        {
            return url.Error;
        }

        if (command.Name is not null && !RepositoryName.Create(command.Name).IsSuccess)
        {
            return DomainError.Validation("Repository name must not be empty.");
        }

        string baseBranch;
        try
        {
            baseBranch = string.IsNullOrWhiteSpace(command.BaseBranch)
                ? await remote.GetDefaultBranchAsync(url.Value.Value, cancellationToken).ConfigureAwait(false)
                : command.BaseBranch.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DomainError.Validation($"Cannot reach {url.Value}: {ex.Message}");
        }

        var tag = command.MatchTag;
        if (string.IsNullOrWhiteSpace(tag) && (command.MatchAreaPaths is null || command.MatchAreaPaths.Count == 0))
        {
            // Default match rule: a repo:<name> tag, so a new registration is usable immediately.
            tag = $"repo:{command.Name ?? url.Value.AzureDevOps.Name}";
        }

        var repository = Repository.From(url.Value, command.Name, baseBranch, tag, command.MatchAreaPaths);
        var saved = await registry.UpsertAsync(repository, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        await worktrees.EnsureCloneAsync(repository, cancellationToken).ConfigureAwait(false);
        return repository;
    }
}

public sealed record RemoveRepository(RepositoryName Name);

public sealed class RemoveRepositoryHandler(IRepositoryRegistry registry) : ICommandHandler<RemoveRepository, Unit>
{
    public async Task<Result<Unit>> Handle(RemoveRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await registry.RemoveAsync(command.Name, cancellationToken).ConfigureAwait(false)
            ? Unit.Value
            : DomainError.NotFound($"Repository '{command.Name}'");
    }
}
