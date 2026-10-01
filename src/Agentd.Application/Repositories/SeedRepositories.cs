using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Repositories;

/// <summary>Register the repositories declared in configuration (<c>Agentd:Repositories:Items</c>).</summary>
public sealed record SeedRepositories;

/// <summary>Seeded repository names, and one message per entry that could not be registered.</summary>
public sealed record SeedResult(IReadOnlyList<string> Seeded, IReadOnlyList<string> Errors);

public sealed class SeedRepositoriesHandler(
    IRepositoryRegistry registry,
    ICommandHandler<AddRepository, Repository> add,
    IOptions<RepositorySeedOptions> options) : ICommandHandler<SeedRepositories, SeedResult>
{
    public async Task<Result<SeedResult>> Handle(SeedRepositories command, CancellationToken cancellationToken)
    {
        var existing = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var seeded = new List<string>();
        var errors = new List<string>();
        foreach (var seed in options.Value.Items)
        {
            // Already registered with the same URL and settings: skip the network round trip.
            if (RemoteUrl.Parse(seed.Url) is { IsSuccess: true } url
                && existing.Any(r => string.Equals(r.RemoteUrl, url.Value.Value, StringComparison.Ordinal) && Matches(r, seed)))
            {
                continue;
            }

            var result = await add.Handle(
                new AddRepository(seed.Url, seed.Name, seed.BaseBranch, seed.MatchTag, seed.MatchAreaPaths.ToList()),
                cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                seeded.Add(result.Value.Name.Value);
            }
            else
            {
                errors.Add($"{seed.Url}: {result.Error.Message}");
            }
        }

        return new SeedResult(seeded, errors);
    }

    private static bool Matches(Repository repository, RepositorySeed seed) =>
        (seed.Name is null || string.Equals(repository.Name.Value, seed.Name, StringComparison.Ordinal))
        && (seed.BaseBranch is null || string.Equals(repository.BaseBranch, seed.BaseBranch, StringComparison.Ordinal))
        && (seed.MatchTag is null || string.Equals(repository.MatchTag, seed.MatchTag, StringComparison.OrdinalIgnoreCase));
}
