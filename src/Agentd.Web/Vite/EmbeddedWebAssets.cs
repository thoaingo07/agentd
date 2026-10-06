using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Agentd.Web.Vite;

/// <summary>
/// The Vite build (<c>wwwroot/**</c>) embedded in this assembly, served under <c>/_content/Agentd.Web/</c>. It's the
/// fallback after the real web root, so the single-file <c>agentd</c> (T1b.6) needs nothing next to it, while
/// development and <c>dotnet run</c> keep serving the files on disk.
/// </summary>
public sealed class EmbeddedWebAssets(Assembly assembly, string requestPrefix = EmbeddedWebAssets.ContentPrefix) : IFileProvider
{
    public const string ContentPrefix = "_content/Agentd.Web/";
    public const string ResourcePrefix = "wwwroot/";

    private readonly HashSet<string> _resources = new(assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)), StringComparer.Ordinal);
    private readonly DateTimeOffset _modified = Built();

    /// <summary>False when the assembly was built without the web (e.g. the .NET-only CI job).</summary>
    public bool HasAssets => _resources.Count > 0;

    public IFileInfo GetFileInfo(string subpath)
    {
        var path = (subpath ?? string.Empty).TrimStart('/');
        if (!path.StartsWith(requestPrefix, StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
        {
            return new NotFoundFileInfo(subpath ?? string.Empty);
        }

        var resource = ResourcePrefix + path[requestPrefix.Length..];
        return _resources.Contains(resource) ? new Resource(assembly, resource, _modified) : new NotFoundFileInfo(subpath!);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    /// <summary>The binary's time stamp (the build), for Last-Modified and ETags.</summary>
    private static DateTimeOffset Built() =>
        Environment.ProcessPath is { } exe && File.Exists(exe) ? File.GetLastWriteTimeUtc(exe) : DateTimeOffset.UnixEpoch;

    private sealed class Resource(Assembly assembly, string name, DateTimeOffset modified) : IFileInfo
    {
        private readonly Lazy<long> _length = new(() =>
        {
            using var s = assembly.GetManifestResourceStream(name)!;
            return s.Length;
        });

        public bool Exists => true;

        public long Length => _length.Value;

        public string? PhysicalPath => null;

        public string Name => Path.GetFileName(name);

        public DateTimeOffset LastModified => modified;

        public bool IsDirectory => false;

        public Stream CreateReadStream() => assembly.GetManifestResourceStream(name)!;
    }
}
