using Agentd.Domain.Common;

namespace Agentd.Application.Setup;

/// <summary>
/// Settings → Folders: where agentd keeps job worktrees and its repository clones (<c>agentd.json</c>, used after a restart).
/// <c>…OverriddenBy</c> names what supplies a value instead (an environment variable): saving here then changes nothing.
/// </summary>
public sealed record FoldersStep(string? Worktrees, string? Repositories, string? WorktreesOverriddenBy, string? RepositoriesOverriddenBy);

/// <param name="Worktrees">The worktree root (absolute, or under <c>~/</c>); empty goes back to the default in agentd's home.</param>
/// <param name="Repositories">The clones' folder, the same way.</param>
public sealed record FoldersInput(string? Worktrees, string? Repositories);

public interface IFolderProbe
{
    /// <summary>Whether agentd can use <paramref name="path"/> (<c>~</c> expanded): it exists or can be created, and agentd can write there.</summary>
    StepCheck Test(string path);
}

/// <summary>The folders agentd works in (docs/architect/deployment.md §5): Save, Test, and what's set now.</summary>
public sealed class FolderSettings(IConfigWriter config, ISettingsAudit audit, IFolderProbe probe, TimeProvider time)
{
    public const string WorktreesKey = "Agentd:Git:WorktreeRoot";
    public const string RepositoriesKey = "Agentd:Git:RepositoriesRoot";

    public FoldersStep Get() => new(Blank(config.Read(WorktreesKey)), Blank(config.Read(RepositoriesKey)), config.OverriddenBy(WorktreesKey), config.OverriddenBy(RepositoriesKey));

    /// <summary>Writes both folders to agentd.json; existing worktrees and clones stay where they are, new ones go to the new folders after a restart.</summary>
    public async Task<Result<SaveResult>> SaveAsync(FoldersInput input, string by, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Invalid(input) is { } problem)
        {
            return DomainError.Validation(problem);
        }

        var values = new Dictionary<string, string?> { [WorktreesKey] = Blank(input.Worktrees), [RepositoriesKey] = Blank(input.Repositories) };
        var changes = values.Where(v => !string.Equals(Blank(config.Read(v.Key)), v.Value, StringComparison.Ordinal))
            .Select(v => new SettingsChange(v.Key, "setting", v.Value is null ? "removed" : "set", by, time.GetUtcNow())).ToList();
        await config.SetAsync(values, cancellationToken).ConfigureAwait(false);
        if (changes.Count > 0)
        {
            await audit.RecordAsync(changes, cancellationToken).ConfigureAwait(false);
        }

        return new SaveResult(RestartRequired: changes.Count > 0);
    }

    /// <summary>Checks each folder given (empty: the one set now). Nothing is saved.</summary>
    public StepCheck Test(FoldersInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Invalid(input) is { } problem)
        {
            return new StepCheck(false, problem);
        }

        foreach (var (name, path) in new[] { ("Worktrees", Blank(input.Worktrees) ?? Blank(config.Read(WorktreesKey))), ("Repositories", Blank(input.Repositories) ?? Blank(config.Read(RepositoriesKey))) })
        {
            if (path is not null && probe.Test(path) is { Ok: false } failed)
            {
                return failed with { Message = $"{name}: {failed.Message}" };
            }
        }

        return new StepCheck(true, "agentd can use both folders.");
    }

    /// <summary>Why a folder can't be used as written, or null: each is absolute or under <c>~/</c>, without <c>..</c>.</summary>
    private static string? Invalid(FoldersInput input) =>
        new[] { Blank(input.Worktrees), Blank(input.Repositories) }.OfType<string>().Any(p => !IsUsable(p))
            ? "Use a full path (e.g. /srv/agentd/worktrees) or one under your home (~/agentd/worktrees), without '..'."
            : null;

    private static bool IsUsable(string path) =>
        (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || Path.IsPathFullyQualified(path))
        && !path.Replace('\\', '/').Split('/').Contains("..");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
