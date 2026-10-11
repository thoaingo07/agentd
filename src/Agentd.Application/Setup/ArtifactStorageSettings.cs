using Agentd.Application.Artifacts;
using Agentd.Domain.Common;

namespace Agentd.Application.Setup;

/// <summary>
/// Settings → Artifacts: the S3 API URL and the public domain (<c>agentd.json</c>), the access keys as statuses only.
/// <c>…OverriddenBy</c> names what supplies a value instead (Key Vault, an environment variable): saving here then changes nothing.
/// </summary>
public sealed record ArtifactStorageStep(string? Url, string? PublicUrl, string? UrlOverriddenBy, string? PublicUrlOverriddenBy, SecretStatus AccessKeyId, SecretStatus SecretAccessKey);

/// <param name="Url">The S3 API URL ending with the bucket; empty keeps new pages in the database.</param>
/// <param name="PublicUrl">Where the bucket's pages are public (its custom domain); empty links them to agentd.</param>
/// <param name="AccessKeyId">A new access key ID; empty keeps the stored one.</param>
/// <param name="SecretAccessKey">A new secret access key; empty keeps the stored one.</param>
public sealed record ArtifactStorageInput(string? Url, string? PublicUrl, string? AccessKeyId, string? SecretAccessKey)
{
    /// <summary>Never print the keys (records print every property).</summary>
    public override string ToString() => $"ArtifactStorageInput {{ Url = {Url}, PublicUrl = {PublicUrl} }}";
}

public interface IArtifactStorageProbe
{
    /// <summary>Uploads, reads back and deletes a small page with <paramref name="options"/>; with a public URL, also opens it there.</summary>
    Task<StepCheck> TestAsync(ArtifactS3Options options, CancellationToken cancellationToken);
}

/// <summary>Where artifact pages are stored and linked (docs/architect/artifacts.md): Save, Test, and what's set now.</summary>
public sealed class ArtifactStorageSettings(IConfigWriter config, ISecrets secrets, ISettingsAudit audit, IArtifactStorageProbe probe, TimeProvider time)
{
    public const string UrlKey = "Artifacts:S3:Url";
    public const string PublicUrlKey = "Artifacts:S3:PublicUrl";
    public const string AccessKeyIdSecret = "Artifacts:S3:AccessKeyId";
    public const string SecretAccessKeySecret = "Artifacts:S3:SecretAccessKey";

    public ArtifactStorageStep Get() => new(
        Blank(config.Read(UrlKey)), Blank(config.Read(PublicUrlKey)), config.OverriddenBy(UrlKey), config.OverriddenBy(PublicUrlKey),
        secrets.Status(AccessKeyIdSecret), secrets.Status(SecretAccessKeySecret));

    /// <summary>Writes both URLs to agentd.json and any new key to the secret store; agentd uses them after a restart.</summary>
    public async Task<Result<SaveResult>> SaveAsync(ArtifactStorageInput input, string by, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var resolved = Resolve(input);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        var changes = new List<SettingsChange>();
        foreach (var (key, value) in new[] { (UrlKey, resolved.Value.Url), (PublicUrlKey, resolved.Value.PublicUrl) })
        {
            if (!string.Equals(Blank(config.Read(key)), value, StringComparison.Ordinal))
            {
                changes.Add(Change(key, "setting", value is null ? "removed" : "set", by));
            }
        }

        await config.SetAsync(new Dictionary<string, string?> { [UrlKey] = resolved.Value.Url, [PublicUrlKey] = resolved.Value.PublicUrl }, cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in new[] { (AccessKeyIdSecret, Blank(input.AccessKeyId)), (SecretAccessKeySecret, Blank(input.SecretAccessKey)) })
        {
            if (value is not null)
            {
                secrets.Store(key, value, by);
                changes.Add(Change(key, "secret", "set", by));
            }
        }

        if (changes.Count > 0)
        {
            await audit.RecordAsync(changes, cancellationToken).ConfigureAwait(false);
        }

        return new SaveResult(RestartRequired: true);
    }

    /// <summary>Tries the given settings (empty keys: the stored ones). Nothing is saved.</summary>
    public async Task<StepCheck> TestAsync(ArtifactStorageInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var resolved = Resolve(input);
        if (!resolved.IsSuccess)
        {
            return new StepCheck(false, resolved.Error.Message);
        }

        return resolved.Value.Url is null
            ? new StepCheck(false, "Enter the S3 API URL to test it. Without one, pages are kept in the database.")
            : await probe.TestAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The settings to save or test, with the stored keys where none were given.</summary>
    private Result<ArtifactS3Options> Resolve(ArtifactStorageInput input)
    {
        var url = Blank(input.Url)?.TrimEnd('/');
        var publicUrl = Blank(input.PublicUrl)?.TrimEnd('/');
        if (url is not null && (!IsHttp(url, out var api) || api.AbsolutePath.Trim('/') is not { Length: > 0 } bucket || bucket.Contains('/', StringComparison.Ordinal)))
        {
            return DomainError.Validation("The S3 API URL ends with the bucket, e.g. https://<account>.r2.cloudflarestorage.com/agentd.");
        }

        if (publicUrl is not null && (!IsHttp(publicUrl, out var link) || link.Query.Length > 0))
        {
            return DomainError.Validation("The public domain is a web address, e.g. https://agentd.example.com.");
        }

        var options = new ArtifactS3Options
        {
            Url = url,
            PublicUrl = publicUrl,
            AccessKeyId = Blank(input.AccessKeyId) ?? secrets.TryGet(AccessKeyIdSecret),
            SecretAccessKey = Blank(input.SecretAccessKey) ?? secrets.TryGet(SecretAccessKeySecret),
        };
        return url is not null && !options.Configured
            ? DomainError.Validation("Add the access key ID and secret access key (in Cloudflare: R2 → Manage API tokens, Object Read & Write on the bucket).")
            : options;
    }

    private static bool IsHttp(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private SettingsChange Change(string key, string kind, string action, string by) => new(key, kind, action, by, time.GetUtcNow());
}
