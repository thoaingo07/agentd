using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Agentd.Application.Reviews;
using Agentd.Domain.Repositories;

namespace Agentd.Infrastructure.Git;

/// <summary>
/// <see cref="IBranchSearch"/> with git on the repository's clone, at a commit (never the working tree): <c>ls-tree</c> for
/// file names, <c>grep --fixed-strings</c> for text, <c>cat-file</c> to read a file. Arguments only, never a shell: the
/// search text goes after <c>-e</c>, a path only after the commit (<c>&lt;commit&gt;:&lt;path&gt;</c>).
/// </summary>
public sealed partial class GitBranchSearch(GitCli git, GitWorktreeManager worktrees) : IBranchSearch
{
    /// <summary>Matches per file (git grep's --max-count), so one noisy file doesn't fill the results.</summary>
    public const int MaxPerFile = 20;

    /// <summary>A file bigger than this isn't read at all (its size is said instead).</summary>
    public const long MaxReadBytes = 8 * 1024 * 1024;

    private const int SnippetBefore = 80;
    private const int SnippetAfter = 160;

    // A commit's file list doesn't change: the last few are kept, so typing a search reads the tree once.
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _trees = new();

    public async Task<IReadOnlyList<string>> ListFilesAsync(Repository repository, string commit, CancellationToken cancellationToken)
    {
        var clone = worktrees.ClonePathFor(repository);
        var key = $"{clone}@{Commit(commit)}";
        if (_trees.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = await git.RunAsync(clone, ["ls-tree", "-r", "-z", "--name-only", "--full-tree", Commit(commit)], cancellationToken).ConfigureAwait(false);
        var paths = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (_trees.Count >= 8)
        {
            _trees.Clear();
        }

        _trees[key] = paths;
        return paths;
    }

    public async Task<IReadOnlyList<TextHit>> GrepAsync(Repository repository, string commit, string text, bool ignoreCase, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        string[] args = ["grep", "-n", "-I", "--null", "--fixed-strings", $"--max-count={MaxPerFile}", .. ignoreCase ? new[] { "-i" } : [], "-e", text, Commit(commit), "--"];
        var result = await git.RunAsync(worktrees.ClonePathFor(repository), args, cancellationToken, throwOnError: false).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 1))
        {
            throw new GitException($"git grep failed ({result.ExitCode}): {result.StandardError}", result.ExitCode, result.StandardError);
        }

        // <commit>:<path>\0<line>\0<text>
        var hits = new List<TextHit>();
        var prefix = Commit(commit) + ":";
        foreach (var row in result.StandardOutput.Split('\n'))
        {
            var parts = row.Split('\0', 3);
            if (parts.Length < 3 || !parts[0].StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var line))
            {
                continue;
            }

            hits.Add(Hit(parts[0][prefix.Length..], line, parts[2].TrimEnd('\r'), text, ignoreCase));
            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    public async Task<CommitFile?> ReadFileAsync(Repository repository, string commit, string path, int maxBytes, CancellationToken cancellationToken)
    {
        var clone = worktrees.ClonePathFor(repository);
        var spec = $"{Commit(commit)}:{path}";
        var type = await git.RunAsync(clone, ["cat-file", "-t", spec], cancellationToken, throwOnError: false).ConfigureAwait(false);
        if (type.ExitCode != 0 || type.StandardOutput != "blob")
        {
            return null;
        }

        var size = await git.RunAsync(clone, ["cat-file", "-s", spec], cancellationToken).ConfigureAwait(false);
        if (long.TryParse(size.StandardOutput, CultureInfo.InvariantCulture, out var bytes) && bytes > MaxReadBytes)
        {
            return new CommitFile(path, string.Empty, Truncated: true, Binary: false);
        }

        var blob = await git.RunAsync(clone, ["cat-file", "blob", spec], cancellationToken).ConfigureAwait(false);
        var content = blob.StandardOutput;
        if (content.AsSpan(0, Math.Min(content.Length, 8000)).Contains('\0'))
        {
            return new CommitFile(path, string.Empty, Truncated: false, Binary: true);
        }

        return content.Length > maxBytes
            ? new CommitFile(path, content[..maxBytes], Truncated: true, Binary: false)
            : new CommitFile(path, content, Truncated: false, Binary: false);
    }

    /// <summary>The line around the match (a long or minified line is cut), and where the match sits in the snippet.</summary>
    internal static TextHit Hit(string path, int line, string text, string query, bool ignoreCase)
    {
        var at = text.IndexOf(query, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (at < 0)
        {
            return new TextHit(path, line, text.Length > SnippetAfter ? text[..SnippetAfter] + "…" : text, 0, 0);
        }

        var start = Math.Max(0, at - SnippetBefore);
        var end = Math.Min(text.Length, at + query.Length + SnippetAfter);
        var lead = start > 0 ? "…" : string.Empty;
        var snippet = lead + text[start..end] + (end < text.Length ? "…" : string.Empty);
        return new TextHit(path, line, snippet, at - start + lead.Length, query.Length);
    }

    /// <summary>A commit id, checked (it's put first in <c>&lt;commit&gt;:&lt;path&gt;</c>, so it can't start an option).</summary>
    private static string Commit(string commit) =>
        Sha().IsMatch(commit) ? commit : throw new ArgumentException($"'{commit}' isn't a commit id.", nameof(commit));

    [GeneratedRegex("^[0-9a-f]{7,64}$")]
    private static partial Regex Sha();
}
