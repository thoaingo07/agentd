using System.Security.Cryptography;
using Agentd.Application.Artifacts;
using Agentd.Application.Setup;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Persistence.Repositories;

/// <summary>
/// Settings → Artifacts → Test: a small page through <see cref="S3ArtifactBlobs"/> (upload, read back, delete), opened on
/// the public domain too when one is given. Messages name what failed, never the keys.
/// </summary>
public sealed class S3ArtifactStorageProbe(HttpClient? http = null) : IArtifactStorageProbe
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<StepCheck> TestAsync(ArtifactS3Options options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var name = $"agentd-check-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        using var blobs = new S3ArtifactBlobs(Options.Create(new ArtifactOptions { S3 = options }));
        var uploaded = false;
        try
        {
            await blobs.PutAsync(name, "<!doctype html>\n<title>agentd check</title>\n<p>agentd's storage check; it's deleted right away.</p>\n", cancellationToken).ConfigureAwait(false);
            uploaded = true;
            if (await blobs.GetAsync(name, cancellationToken).ConfigureAwait(false) is null)
            {
                return new StepCheck(false, "The test page was uploaded but couldn't be read back.", "Check that the token may read objects (Object Read & Write).");
            }

            if (options.PublicUrl is not { Length: > 0 } publicUrl)
            {
                return new StepCheck(true, "Uploaded, read back and deleted a test page. Links stay on agentd.");
            }

            var link = $"{publicUrl.TrimEnd('/')}/{S3ArtifactBlobs.Prefix}{name}.html";
            try
            {
                using var response = await (http ?? s_http).GetAsync(new Uri(link), cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode
                    ? new StepCheck(true, $"Uploaded, read back and deleted a test page, and opened it on {publicUrl}.")
                    : new StepCheck(false, $"Storage works, but {publicUrl} answered {(int)response.StatusCode} for the test page.", PublicFix);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new StepCheck(false, $"Storage works, but {publicUrl} couldn't be reached ({ex.GetType().Name}).", PublicFix);
            }
        }
        catch (AmazonS3Exception ex)
        {
            return new StepCheck(false, $"The storage refused it: {ex.ErrorCode ?? ((int)ex.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)}.", Fix(ex.ErrorCode));
        }
        catch (Exception ex) when (ex is AmazonClientException or HttpRequestException or AmazonServiceException)
        {
            return new StepCheck(false, $"The storage couldn't be reached ({ex.GetBaseException().GetType().Name}).", "Check the S3 API URL (https://<account>.r2.cloudflarestorage.com/<bucket>).");
        }
        finally
        {
            if (uploaded)
            {
                try
                {
                    await blobs.DeleteAsync(name, CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // a leftover test page only costs a few bytes
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
    }

    private const string PublicFix = "Connect the domain to the bucket (Cloudflare: R2 → the bucket → Settings → Custom Domains) and wait until it's Active.";

    private static string Fix(string? code) => code switch
    {
        "NoSuchBucket" => "The URL's path must be the bucket's name, e.g. …r2.cloudflarestorage.com/agentd.",
        "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "AccessDenied" or "Unauthorized" =>
            "Check the access key ID and secret: an R2 API token with Object Read & Write on this bucket.",
        _ => "Check the URL and the keys.",
    };
}
