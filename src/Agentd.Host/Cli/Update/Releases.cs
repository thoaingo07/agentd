using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agentd.Host.Cli.Update;

/// <summary>A semantic version (<c>1.2.3</c>, <c>1.2.3-beta.4</c>; build metadata after <c>+</c> is ignored).</summary>
internal sealed partial record SemVersion(int Major, int Minor, int Patch, string? Pre) : IComparable<SemVersion>
{
    public static SemVersion? Parse(string? text)
    {
        var match = Pattern().Match((text ?? string.Empty).Trim().TrimStart('v'));
        return match.Success
            ? new(Int(match, "major"), Int(match, "minor"), Int(match, "patch"), match.Groups["pre"].Success ? match.Groups["pre"].Value : null)
            : null;
    }

    public bool IsPrerelease => Pre is not null;

    /// <summary>SemVer precedence: a pre-release sorts before its release; identifiers compare numerically when both are numbers.</summary>
    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0 || Pre == other.Pre)
        {
            return core;
        }

        if (Pre is null || other.Pre is null)
        {
            return Pre is null ? 1 : -1;
        }

        var (a, b) = (Pre.Split('.'), other.Pre.Split('.'));
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var cmp = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x) && int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                ? x.CompareTo(y)
                : string.CompareOrdinal(a[i], b[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => Pre is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";

    private static int Int(Match m, string group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<major>\d{1,6})\.(?<minor>\d{1,6})\.(?<patch>\d{1,6})(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();
}

/// <summary>A published release and its downloadable files.</summary>
internal sealed record Release(string Tag, SemVersion Version, bool Prerelease, IReadOnlyDictionary<string, Uri> Assets);

/// <summary>GitHub Releases of agentd's repository (public; an optional <c>GITHUB_TOKEN</c> only raises the rate limit).</summary>
internal sealed class GitHubReleases(HttpClient http, string repository, string? token = null)
{
    public const string DefaultRepository = "thoaingo07/agentd";

    public async Task<IReadOnlyList<Release>> ListAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.github.com/repos/{repository}/releases?per_page=30"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("agentd", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var releases = new List<Release>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean() || SemVersion.Parse(r.GetProperty("tag_name").GetString()) is not { } version)
            {
                continue;
            }

            var assets = r.GetProperty("assets").EnumerateArray()
                .ToDictionary(a => a.GetProperty("name").GetString()!, a => new Uri(a.GetProperty("browser_download_url").GetString()!), StringComparer.Ordinal);
            releases.Add(new Release(r.GetProperty("tag_name").GetString()!, version, r.GetProperty("prerelease").GetBoolean() || version.IsPrerelease, assets));
        }

        return releases;
    }

    /// <summary>The release to move to: <paramref name="wanted"/> if given; otherwise the newest on the channel that's newer than <paramref name="current"/>.</summary>
    public static Release? Pick(IReadOnlyList<Release> releases, string channel, SemVersion current, SemVersion? wanted)
    {
        ArgumentNullException.ThrowIfNull(releases);
        if (wanted is not null)
        {
            return releases.FirstOrDefault(r => r.Version.CompareTo(wanted) == 0);
        }

        return releases
            .Where(r => channel == "beta" || !r.Prerelease)
            .Where(r => r.Version.CompareTo(current) > 0)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
    }
}

/// <summary>Replaces the running executable with a release's build for this platform, after checking it against sha256sums.txt.</summary>
internal sealed class Updater(HttpClient http, string executable, string runtime)
{
    public string AssetName => runtime.StartsWith("win", StringComparison.Ordinal) ? $"agentd-{runtime}.exe" : $"agentd-{runtime}";

    /// <summary>Downloads, verifies and swaps in the new binary; the old one is kept as <c>agentd.previous</c>. Returns the problem, or null.</summary>
    public async Task<string?> ApplyAsync(Release release, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!release.Assets.TryGetValue(AssetName, out var binaryUrl) || !release.Assets.TryGetValue("sha256sums.txt", out var sumsUrl))
        {
            return $"release {release.Tag} has no {AssetName} (or no sha256sums.txt)";
        }

        var sums = await http.GetStringAsync(sumsUrl, ct).ConfigureAwait(false);
        var expected = sums.Split('\n').Select(l => l.Split(' ', 2, StringSplitOptions.TrimEntries)).FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == AssetName)?[0];
        if (expected is null)
        {
            return $"sha256sums.txt has no line for {AssetName}";
        }

        var download = executable + ".download";
        try
        {
            await using (var target = File.Create(download))
            {
                await using var source = await http.GetStreamAsync(binaryUrl, ct).ConfigureAwait(false);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            string actual;
            await using (var check = File.OpenRead(download))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return $"the download doesn't match sha256sums.txt (expected {expected[..12]}…, got {actual[..12]}…); nothing was changed";
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(download, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            }

            // Renaming works on a running executable (also on Windows); the old binary stays as agentd.previous.
            File.Move(executable, Previous(executable), overwrite: true);
            File.Move(download, executable);
            return null;
        }
        finally
        {
            File.Delete(download);
        }
    }

    public static string Previous(string executable) =>
        Path.Combine(Path.GetDirectoryName(executable)!, Path.GetFileNameWithoutExtension(executable) + ".previous" + Path.GetExtension(executable));
}
