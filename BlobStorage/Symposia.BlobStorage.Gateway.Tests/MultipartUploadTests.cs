using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Issue #5: S3 multipart upload through the official AWS SDK against the in-process gateway.</summary>
[Collection("GatewayIntegration")]
public sealed class MultipartUploadTests(GatewayFixture f) : IClassFixture<GatewayFixture>
{
    private const string Bucket = "test-bucket";
    private const int FiveMiB = 5 * 1024 * 1024;

    private static string Key() => "mp/" + Guid.NewGuid().ToString("N");

    private static byte[] Random(int size)
    {
        var data = new byte[size];
        System.Random.Shared.NextBytes(data);
        return data;
    }

    private async Task<string> StartAsync(string key) =>
        (await f.S3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = Bucket, Key = key })).UploadId;

    private async Task<PartETag> PartAsync(string key, string uploadId, int number, byte[] data)
    {
        var resp = await f.S3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = Bucket, Key = key, UploadId = uploadId, PartNumber = number, InputStream = new MemoryStream(data),
        });
        return new PartETag(number, resp.ETag);
    }

    private Task<CompleteMultipartUploadResponse> CompleteAsync(string key, string uploadId, params PartETag[] parts) =>
        f.S3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = Bucket, Key = key, UploadId = uploadId, PartETags = [.. parts],
        });

    [Fact]
    public async Task UploadPartsOutOfOrder_Complete_ObjectIsTheConcatenation_WithS3StyleEtag()
    {
        var key = Key();
        var part1 = Random(FiveMiB);
        var part2 = Random(1234);
        var id = await StartAsync(key);

        var e2 = await PartAsync(key, id, 2, part2);
        var e1 = await PartAsync(key, id, 1, part1);
        var done = await CompleteAsync(key, id, e1, e2);

        var expectedEtag = Convert.ToHexStringLower(MD5.HashData(
            [.. SHA256.HashData(part1), .. SHA256.HashData(part2)])) + "-2";
        Assert.Equal($"\"{expectedEtag}\"", done.ETag);

        using var get = await f.S3.GetObjectAsync(Bucket, key);
        using var body = new MemoryStream();
        await get.ResponseStream.CopyToAsync(body);
        Assert.Equal([.. part1, .. part2], body.ToArray());
        // GET/HEAD ETags are stripped by the fixture (the SDK would check them as MD5); LIST carries it.
        var listed = await f.S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = key });
        Assert.Equal(done.ETag, Assert.Single(listed.S3Objects).ETag);
    }

    [Fact]
    public async Task ReuploadedPart_ReplacesTheEarlierOne()
    {
        var key = Key();
        var id = await StartAsync(key);
        await PartAsync(key, id, 1, Random(100));
        var final = Random(200);
        var e1 = await PartAsync(key, id, 1, final);

        var parts = await f.S3.ListPartsAsync(new ListPartsRequest { BucketName = Bucket, Key = key, UploadId = id });
        Assert.Equal(200, Assert.Single(parts.Parts).Size);

        await CompleteAsync(key, id, e1);
        using var get = await f.S3.GetObjectAsync(Bucket, key);
        using var body = new MemoryStream();
        await get.ResponseStream.CopyToAsync(body);
        Assert.Equal(final, body.ToArray());
    }

    [Fact]
    public async Task CompleteRetriedWithTheSameParts_IsIdempotent()
    {
        var key = Key();
        var id = await StartAsync(key);
        var e1 = await PartAsync(key, id, 1, Random(10));

        var first = await CompleteAsync(key, id, e1);
        var again = await CompleteAsync(key, id, e1);

        Assert.Equal(first.ETag, again.ETag);
    }

    [Fact]
    public async Task Complete_RejectsUnknownEtag_WrongOrder_AndSmallNonFinalParts()
    {
        var key = Key();
        var id = await StartAsync(key);
        var e1 = await PartAsync(key, id, 1, Random(10));
        var e2 = await PartAsync(key, id, 2, Random(10));

        var bad = await Assert.ThrowsAsync<AmazonS3Exception>(() => CompleteAsync(key, id, new PartETag(1, "\"" + new string('0', 64) + "\""), e2));
        Assert.Equal("InvalidPart", bad.ErrorCode);

        // The SDK sorts parts before sending, so a repeated part number is how a client gets the order wrong.
        var order = await Assert.ThrowsAsync<AmazonS3Exception>(() => CompleteAsync(key, id, e1, e1));
        Assert.Equal("InvalidPartOrder", order.ErrorCode);

        var small = await Assert.ThrowsAsync<AmazonS3Exception>(() => CompleteAsync(key, id, e1, e2));
        Assert.Equal("EntityTooSmall", small.ErrorCode);

        // The upload survives failed Completes.
        Assert.Equal(2, (await f.S3.ListPartsAsync(new ListPartsRequest { BucketName = Bucket, Key = key, UploadId = id })).Parts.Count);
    }

    [Fact]
    public async Task Abort_RemovesTheUpload()
    {
        var key = Key();
        var id = await StartAsync(key);
        await PartAsync(key, id, 1, Random(10));

        var listed = await f.S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket, Prefix = key });
        Assert.Equal(id, Assert.Single(listed.MultipartUploads).UploadId);

        await f.S3.AbortMultipartUploadAsync(Bucket, key, id);

        var gone = await Assert.ThrowsAsync<AmazonS3Exception>(() => PartAsync(key, id, 2, Random(10)));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("NoSuchUpload", gone.ErrorCode);
        Assert.Empty((await f.S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket, Prefix = key })).MultipartUploads);
        await Assert.ThrowsAsync<AmazonS3Exception>(() => f.S3.GetObjectMetadataAsync(Bucket, key));
    }

    [Fact]
    public async Task PartNumberOutOfRange_IsRejected()
    {
        var key = Key();
        var id = await StartAsync(key);
        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => PartAsync(key, id, 10_001, Random(10)));
        Assert.Equal("InvalidArgument", ex.ErrorCode);
    }

    [Fact]
    public async Task UploadIdForAnotherKey_IsNoSuchUpload()
    {
        var id = await StartAsync(Key());
        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => PartAsync(Key(), id, 1, Random(10)));
        Assert.Equal("NoSuchUpload", ex.ErrorCode);
    }
}
