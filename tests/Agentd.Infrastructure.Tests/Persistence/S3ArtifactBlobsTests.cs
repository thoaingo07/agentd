using Agentd.Application.Artifacts;
using Agentd.Infrastructure.Persistence.Repositories;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.Persistence;

/// <summary>The S3 client as agentd configures it for R2, against a real S3-compatible server (Adobe's S3Mock).</summary>
[TestClass]
[TestCategory("Integration")]
public sealed class S3ArtifactBlobsTests
{
    [TestMethod]
    public async Task Pages_are_put_read_and_deleted_as_html_objects_under_artifacts()
    {
        await using var s3 = new ContainerBuilder("adobe/s3mock:5.2.3")
            .WithEnvironment("COM_ADOBE_TESTING_S3MOCK_STORE_INITIAL_BUCKETS", "agentd")
            .WithPortBinding(9090, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/")))
            .Build();
        await s3.StartAsync();
        var endpoint = $"http://{s3.Hostname}:{s3.GetMappedPublicPort(9090)}";
        var credentials = new BasicAWSCredentials("test-key", "test-secret");   // S3Mock accepts any

        using var blobs = new S3ArtifactBlobs(Options.Create(new ArtifactOptions
        {
            S3 = new ArtifactS3Options { Url = $"{endpoint}/agentd", AccessKeyId = "test-key", SecretAccessKey = "test-secret" },
        }));

        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => blobs.PutAsync($"page-{i}", $"<p>café {i}</p>", default)));
        var read = await blobs.GetAsync("page-3", default);
        await blobs.DeleteAsync("page-3", default);
        var gone = await blobs.GetAsync("page-3", default);

        Assert.AreEqual("<p>café 3</p>", read, "UTF-8 round trip");
        Assert.IsNull(gone);
        using var check = new AmazonS3Client(credentials, new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
        var head = await check.GetObjectMetadataAsync("agentd", "artifacts/page-1.html");
        Assert.AreEqual("text/html; charset=utf-8", head.Headers.ContentType);
        Assert.AreEqual(4, (await check.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "agentd", Prefix = "artifacts/" })).S3Objects.Count);
    }

    [TestMethod]
    public async Task The_settings_test_uploads_reads_opens_publicly_and_deletes_and_says_what_failed()
    {
        await using var s3 = new ContainerBuilder("adobe/s3mock:5.2.3")
            .WithEnvironment("COM_ADOBE_TESTING_S3MOCK_STORE_INITIAL_BUCKETS", "agentd")
            .WithPortBinding(9090, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/")))
            .Build();
        await s3.StartAsync();
        var endpoint = $"http://{s3.Hostname}:{s3.GetMappedPublicPort(9090)}";
        var probe = new S3ArtifactStorageProbe();
        ArtifactS3Options Options(string url, string? publicUrl = null) => new() { Url = url, PublicUrl = publicUrl, AccessKeyId = "test-key", SecretAccessKey = "test-secret" };

        var stored = await probe.TestAsync(Options($"{endpoint}/agentd"), default);
        var published = await probe.TestAsync(Options($"{endpoint}/agentd", $"{endpoint}/agentd/"), default);   // S3Mock serves objects without signing
        var notPublic = await probe.TestAsync(Options($"{endpoint}/agentd", $"{endpoint}/elsewhere"), default);
        var noBucket = await probe.TestAsync(Options($"{endpoint}/nope"), default);
        var unreachable = await probe.TestAsync(Options("http://127.0.0.1:9/agentd"), default);

        Assert.AreEqual((true, "Uploaded, read back and deleted a test page. Links stay on agentd."), (stored.Ok, stored.Message));
        Assert.IsTrue(published.Ok, published.Message);
        StringAssert.Contains(published.Message, "opened it on");
        Assert.AreEqual(false, notPublic.Ok);
        StringAssert.Contains(notPublic.Message, "answered 404");
        StringAssert.Contains(notPublic.Fix, "Custom Domains");
        Assert.AreEqual((false, "The storage refused it: NoSuchBucket."), (noBucket.Ok, noBucket.Message));
        StringAssert.Contains(noBucket.Fix, "bucket's name");
        Assert.AreEqual(false, unreachable.Ok);
        StringAssert.Contains(unreachable.Fix, "S3 API URL");
        using var check = new AmazonS3Client(new BasicAWSCredentials("test-key", "test-secret"), new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
        Assert.IsEmpty((await check.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "agentd", Prefix = "artifacts/" })).S3Objects ?? [], "test pages are deleted");
    }

    [TestMethod]
    [DataRow("https://acct.r2.cloudflarestorage.com")]
    [DataRow("https://acct.r2.cloudflarestorage.com/agentd/more")]
    [DataRow("not a url")]
    public async Task A_url_without_exactly_one_bucket_is_refused(string url)
    {
        using var blobs = new S3ArtifactBlobs(Options.Create(new ArtifactOptions { S3 = new ArtifactS3Options { Url = url, AccessKeyId = "id", SecretAccessKey = "secret" } }));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => blobs.GetAsync("x", default));
    }
}
