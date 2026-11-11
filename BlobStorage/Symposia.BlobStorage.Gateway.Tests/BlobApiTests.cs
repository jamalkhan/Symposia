using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;
using Symposia.BlobStorage.Gateway.Api;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>The Symposia-native /v1 blob API (issue #203) over the same store as the S3 surface.</summary>
[Collection("GatewayIntegration")]
public sealed class BlobApiTests(GatewayFixture f) : IClassFixture<GatewayFixture>
{
    private readonly HttpClient _api = f.SignedClient();

    private static string NewBucket() => "v1-" + Guid.NewGuid().ToString("N")[..12];

    private async Task<string> CreateBucketAsync()
    {
        var bucket = NewBucket();
        (await _api.PutAsync($"/v1/buckets/{bucket}", null)).EnsureSuccessStatusCode();
        return bucket;
    }

    private Task<HttpResponseMessage> PutAsync(string bucket, string key, string body, string contentType = "text/plain")
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return _api.PutAsync($"/v1/buckets/{bucket}/objects/{Uri.EscapeDataString(key).Replace("%2F", "/")}", content);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // AC1
    [Fact]
    public async Task ObjectsWrittenThroughEitherApi_ReadBackThroughTheOther()
    {
        var bucket = await CreateBucketAsync();
        var put = await PutAsync(bucket, "dir/via-v1.txt", "from v1");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("dir/via-v1.txt", (await Json(put)).GetProperty("key").GetString());

        using var viaS3 = await f.S3.GetObjectAsync(bucket, "dir/via-v1.txt");
        Assert.Equal("from v1", await new StreamReader(viaS3.ResponseStream).ReadToEndAsync());

        await f.S3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "via-s3.txt", ContentBody = "from s3", ContentType = "text/plain" });
        var get = await _api.GetAsync($"/v1/buckets/{bucket}/objects/via-s3.txt");
        Assert.Equal("from s3", await get.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", get.Content.Headers.ContentType?.MediaType);
    }

    // AC2
    [Fact]
    public async Task Listing_PagesWithStableCursors_NeverRepeatingOrSkipping()
    {
        var bucket = await CreateBucketAsync();
        var keys = new[] { "a/1", "a/2", "a/3", "a/4", "a/5" };
        foreach (var key in keys)
        {
            (await PutAsync(bucket, key, key)).EnsureSuccessStatusCode();
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Json(await _api.GetAsync($"/v1/buckets/{bucket}/objects?limit=2" + (cursor is null ? "" : $"&cursor={cursor}")));
            seen.AddRange(page.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("key").GetString()!));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(keys, seen);
    }

    [Fact]
    public async Task PrefixListing_IsCaseSensitiveAndExact()
    {
        var bucket = await CreateBucketAsync();
        foreach (var key in new[] { "Apple", "apple", "app", "b" })
        {
            (await PutAsync(bucket, key, key)).EnsureSuccessStatusCode();
        }

        var page = await Json(await _api.GetAsync($"/v1/buckets/{bucket}/objects?prefix=app"));
        Assert.Equal(["app", "apple"], page.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("key").GetString()!));

        // The same rule on the S3 surface (it used to fold case).
        var s3 = await f.S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "A" });
        Assert.Equal(["Apple"], s3.S3Objects.Select(o => o.Key));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("ten")]
    public async Task InvalidLimit_Is400(string limit)
    {
        var bucket = await CreateBucketAsync();
        var r = await _api.GetAsync($"/v1/buckets/{bucket}/objects?limit={limit}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("InvalidArgument", (await Json(r)).GetProperty("error").GetProperty("code").GetString());
    }

    // AC3
    [Fact]
    public async Task BadOrMissingSignatures_Are401Json_ExceptTheOpenApiDocument()
    {
        var wrong = f.SignedClient(secretKey: "not-the-secret");
        var r = await wrong.GetAsync("/v1/buckets");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var error = (await Json(r)).GetProperty("error");
        Assert.Equal("AccessDenied", error.GetProperty("code").GetString());
        Assert.Equal(r.Headers.GetValues(RequestIds.Header).Single(), error.GetProperty("requestId").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.GatewayHttpClient.GetAsync("/v1/buckets")).StatusCode);
        var doc = await f.GatewayHttpClient.GetAsync(BlobApiEndpoints.OpenApiPath);
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
        Assert.Equal("3.1.0", (await Json(doc)).GetProperty("openapi").GetString());
    }

    // AC4
    [Fact]
    public async Task DeletingANonEmptyBucket_Is409BucketNotEmpty()
    {
        var bucket = await CreateBucketAsync();
        (await PutAsync(bucket, "k", "v")).EnsureSuccessStatusCode();

        var r = await _api.DeleteAsync($"/v1/buckets/{bucket}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("BucketNotEmpty", (await Json(r)).GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/v1/buckets/{bucket}/objects/k")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/v1/buckets/{bucket}/objects/k")).StatusCode); // idempotent
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/v1/buckets/{bucket}")).StatusCode);
    }

    // AC5
    [Fact]
    public async Task Meta_ReturnsTheCidAndReplicas_FromTheMetadataRow()
    {
        var bucket = await CreateBucketAsync();
        var written = await Json(await PutAsync(bucket, "with/meta", "metadata please"));

        var meta = await Json(await _api.GetAsync($"/v1/buckets/{bucket}/objects/with/meta?meta"));

        Assert.Equal(written.GetProperty("cid").GetString(), meta.GetProperty("cid").GetString());
        Assert.Equal(15, meta.GetProperty("size").GetInt64());
        Assert.NotEmpty(meta.GetProperty("replicas").EnumerateArray());
    }

    [Fact]
    public async Task ErrorsAndNames_FollowS3()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PutAsync("/v1/buckets/Not_Valid", null)).StatusCode);
        var missing = await _api.GetAsync($"/v1/buckets/{NewBucket()}/objects/nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("NoSuchKey", (await Json(missing)).GetProperty("error").GetProperty("code").GetString());

        var bucket = await CreateBucketAsync();
        Assert.Equal(HttpStatusCode.OK, (await _api.PutAsync($"/v1/buckets/{bucket}", null)).StatusCode); // idempotent
        var listed = await Json(await _api.GetAsync("/v1/buckets"));
        Assert.Contains(listed.GetProperty("buckets").EnumerateArray(), b => b.GetProperty("name").GetString() == bucket);
    }

    [Fact]
    public async Task S3Responses_CarryRequestIds()
    {
        var r = await f.GatewayHttpClient.GetAsync("/test-bucket");
        Assert.Equal(r.Headers.GetValues(RequestIds.Header).Single(), r.Headers.GetValues("x-amz-request-id").Single());
    }
}
