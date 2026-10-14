using System.Text;
using System.Text.RegularExpressions;
using Agentd.Application.Artifacts;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>
/// Artifact pages as raw HTML files in <see cref="ArtifactOptions.Folder"/> (no S3 set up): <c>&lt;name&gt;.html</c>, written to a
/// temporary file and moved into place so a reader never sees half a page. Names are a page's own (<c>slug-hash</c>);
/// anything else is refused, so no name can reach outside the folder.
/// </summary>
public sealed partial class LocalArtifactFiles(IOptions<ArtifactOptions> options) : IArtifactFiles
{
    public async Task PutAsync(string name, string html, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, html, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var path = PathOf(name);
        try
        {
            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        File.Delete(PathOf(name));   // no file: nothing to do
        return Task.CompletedTask;
    }

    private string PathOf(string name)
    {
        if (!PageName().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' isn't an artifact name.", nameof(name));
        }

        var folder = options.Value.Folder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException("Agentd:Artifacts:Folder isn't set.");
        }

        return Path.Combine(Path.GetFullPath(Home(folder.Trim())), name + ".html");
    }

    /// <summary><c>~</c> or <c>~/…</c> is under the user's home, as in Settings → Folders.</summary>
    private static string Home(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;

    /// <summary>A page's name: lower-case words and its hash, joined by dashes (<see cref="ArtifactService"/>'s slug).</summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex PageName();
}
