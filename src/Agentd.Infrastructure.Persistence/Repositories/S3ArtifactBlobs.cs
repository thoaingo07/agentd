using System.Net;
using Agentd.Application.Artifacts;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>
/// Artifact pages in S3-compatible storage (Cloudflare R2, MinIO, S3): <c>artifacts/&lt;name&gt;.html</c> in the bucket named by
/// the path of <see cref="ArtifactS3Options.Url"/>. R2 wants path-style URLs, the region <c>auto</c>, and no streaming
/// payload signing or default checksums (the SDK's newer defaults it doesn't accept).
/// </summary>
public sealed class S3ArtifactBlobs(IOptions<ArtifactOptions> options) : IArtifactBlobs, IDisposable
{
    public const string Prefix = "artifacts/";

    private readonly Lazy<(AmazonS3Client Client, string Bucket, bool Https)> _s3 = new(() => Create(options.Value.S3));

    public async Task PutAsync(string name, string html, CancellationToken cancellationToken)
    {
        var (client, bucket, https) = _s3.Value;
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = Prefix + name + ".html",
            ContentBody = html,
            ContentType = "text/html; charset=utf-8",
            DisablePayloadSigning = https,   // R2 refuses streaming payload signatures; the SDK allows this only over HTTPS
            DisableDefaultChecksumValidation = true,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var (client, bucket, _) = _s3.Value;
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = Prefix + name + ".html" }, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var (client, bucket, _) = _s3.Value;
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = Prefix + name + ".html" }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_s3.IsValueCreated)
        {
            _s3.Value.Client.Dispose();
        }
    }

    /// <summary>The client and bucket from the S3 API URL (<c>https://&lt;account&gt;.r2.cloudflarestorage.com/&lt;bucket&gt;</c>).</summary>
    internal static (AmazonS3Client Client, string Bucket, bool Https) Create(ArtifactS3Options o)
    {
        if (!o.Configured || !Uri.TryCreate(o.Url, UriKind.Absolute, out var url) || url.AbsolutePath.Trim('/') is not { Length: > 0 } bucket || bucket.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Agentd:Artifacts:S3 needs Url (the S3 API URL ending with the bucket), AccessKeyId and SecretAccessKey.");
        }

        var config = new AmazonS3Config
        {
            ServiceURL = url.GetLeftPart(UriPartial.Authority),
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return (new AmazonS3Client(new BasicAWSCredentials(o.AccessKeyId, o.SecretAccessKey), config), bucket, url.Scheme == Uri.UriSchemeHttps);
    }
}
