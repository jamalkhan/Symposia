using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Symposia.BlobStorage.Gateway.Auth;
using Symposia.BlobStorage.Gateway.S3;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Issue #139: chunk signatures and declared checksums are verified while the upload streams.</summary>
public sealed class UploadIntegrityTests
{
    // AWS SigV4 streaming example ("Signature Calculation: Transfer Payload in Multiple Chunks").
    private const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string AmzDate = "20130524T000000Z";
    private const string Scope = "20130524/us-east-1/s3/aws4_request";
    private const string Seed = "4f232c4386841ef735655705268965c44a0e4690baa4adea153f7db9fa80a0a9";
    private const string Sig1 = "ad80c730a21e5b8d04586a2213dd63b9a0e99e0e2307b0ade35a65485a288648";
    private const string Sig2 = "0055627c9e194cb4542bae2aa5492e3c1575bbb81b612b7d234b86a503ef5497";
    private const string SigFinal = "b6c6ea8a5354eaf15b3cb7646744f4275b71ea724fed81ceb9323e279d449df9";

    private static SigV4Context Context() =>
        new("tenant", "AKIAIOSFODNN7EXAMPLE", SigV4Verifier.SigningKey(Secret, "20130524", "us-east-1", "s3"), AmzDate, Scope, Seed);

    private static byte[] AwsExampleBody(char tamper = 'a', string sig1 = Sig1)
    {
        var body = new MemoryStream();
        void Write(string s) => body.Write(Encoding.ASCII.GetBytes(s));
        Write($"10000;chunk-signature={sig1}\r\n");
        var chunk1 = Enumerable.Repeat((byte)'a', 65536).ToArray();
        chunk1[100] = (byte)tamper;
        body.Write(chunk1);
        Write($"\r\n400;chunk-signature={Sig2}\r\n");
        body.Write(Enumerable.Repeat((byte)'a', 1024).ToArray());
        Write($"\r\n0;chunk-signature={SigFinal}\r\n\r\n");
        return body.ToArray();
    }

    private static async Task<byte[]> Drain(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, 4000);
        return output.ToArray();
    }

    [Fact]
    public async Task AwsSignedChunkExample_Verifies()
    {
        var stream = new AwsChunkedStream(new MemoryStream(AwsExampleBody()), 66560, new ChunkSignatureChain(Context(), trailer: false));
        Assert.Equal(66560, (await Drain(stream)).Length);
    }

    [Fact]
    public async Task TamperedChunkData_IsRejectedWithSignatureDoesNotMatch()
    {
        var stream = new AwsChunkedStream(new MemoryStream(AwsExampleBody(tamper: 'b')), 66560, new ChunkSignatureChain(Context(), trailer: false));
        var ex = await Assert.ThrowsAsync<S3RequestRejectedException>(() => Drain(stream));
        Assert.Equal((403, "SignatureDoesNotMatch"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task AlteredChunkSignature_IsRejected()
    {
        var stream = new AwsChunkedStream(new MemoryStream(AwsExampleBody(sig1: Sig2)), 66560, new ChunkSignatureChain(Context(), trailer: false));
        await Assert.ThrowsAsync<S3RequestRejectedException>(() => Drain(stream));
    }

    [Fact]
    public async Task SignedTrailer_VerifiesAndRejectsTampering()
    {
        var ctx = Context();
        var data = Encoding.ASCII.GetBytes("hello symposia\n");
        var crc = Checksums.ComputeBase64("crc64nvme", data);
        string Body(string trailerValue)
        {
            // Build a valid chain the way a client would, then optionally change the trailer after signing.
            var chain = Sign(ctx.SigningKey, ctx.SeedSignature, "AWS4-HMAC-SHA256-PAYLOAD", data);
            var final = Sign(ctx.SigningKey, chain, "AWS4-HMAC-SHA256-PAYLOAD", []);
            var trailerHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"x-amz-checksum-crc64nvme:{crc}\n")));
            var trailerSig = SigV4Verifier.SignHex(ctx.SigningKey, $"AWS4-HMAC-SHA256-TRAILER\n{AmzDate}\n{Scope}\n{final}\n{trailerHash}");
            return $"f;chunk-signature={chain}\r\nhello symposia\n\r\n0;chunk-signature={final}\r\nx-amz-checksum-crc64nvme:{trailerValue}\r\nx-amz-trailer-signature:{trailerSig}\r\n\r\n";
        }

        var good = new AwsChunkedStream(new MemoryStream(Encoding.ASCII.GetBytes(Body(crc))), 15, new ChunkSignatureChain(ctx, trailer: true));
        Assert.Equal(data, await Drain(good));
        Assert.Equal(crc, good.Trailers["x-amz-checksum-crc64nvme"]);

        var bad = new AwsChunkedStream(new MemoryStream(Encoding.ASCII.GetBytes(Body("AAAAAAAAAAA="))), 15, new ChunkSignatureChain(ctx, trailer: true));
        Assert.Equal(403, (await Assert.ThrowsAsync<S3RequestRejectedException>(() => Drain(bad))).Status);
    }

    private static string Sign(byte[] key, string previous, string algorithm, byte[] data) =>
        SigV4Verifier.SignHex(key,
            $"{algorithm}\n{AmzDate}\n{Scope}\n{previous}\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n{Convert.ToHexStringLower(SHA256.HashData(data))}");

    // Standard CRC check values over "123456789".
    [Theory]
    [InlineData("crc32", "cbf43926")]
    [InlineData("crc32c", "e3069283")]
    [InlineData("crc64nvme", "ae8b14860a799888")]
    public void Crc_MatchesStandardCheckValues(string algorithm, string hex) =>
        Assert.Equal(hex, Convert.ToHexStringLower(Convert.FromBase64String(Checksums.ComputeBase64(algorithm, "123456789"u8))));

    private static HttpContext Request(byte[] body, params (string Name, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(body);
        foreach (var (name, value) in headers)
        {
            ctx.Request.Headers[name] = value;
        }

        return ctx;
    }

    private static readonly byte[] Data = Encoding.ASCII.GetBytes("hello symposia\n");

    [Theory]
    [InlineData("crc32")]
    [InlineData("crc32c")]
    [InlineData("crc64nvme")]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task DeclaredChecksumHeader_PassesWhenRight_AndFailsWithBadDigestWhenWrong(string algorithm)
    {
        var right = Checksums.ComputeBase64(algorithm, Data);
        Assert.Equal(Data, await Drain(UploadBody.Open(Request(Data, ($"x-amz-checksum-{algorithm}", right)))));

        var wrong = Checksums.ComputeBase64(algorithm, "something else"u8);
        var ex = await Assert.ThrowsAsync<S3RequestRejectedException>(() => Drain(UploadBody.Open(Request(Data, ($"x-amz-checksum-{algorithm}", wrong)))));
        Assert.Equal((400, "BadDigest"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task UnsignedChunkedUploadWithWrongTrailingChecksum_IsRejected()
    {
        var wire = Encoding.ASCII.GetBytes("f\r\nhello symposia\n\r\n0\r\nx-amz-checksum-crc64nvme:AAAAAAAAAAA=\r\n\r\n");
        var ctx = Request(wire, ("x-amz-content-sha256", "STREAMING-UNSIGNED-PAYLOAD-TRAILER"), ("Content-Encoding", "aws-chunked"),
            ("x-amz-trailer", "x-amz-checksum-crc64nvme"), ("x-amz-decoded-content-length", "15"));
        Assert.Equal("BadDigest", (await Assert.ThrowsAsync<S3RequestRejectedException>(() => Drain(UploadBody.Open(ctx)))).Code);
    }

    [Fact]
    public async Task UnsignedChunkedUploadWithRightTrailingChecksum_Passes()
    {
        var crc = Checksums.ComputeBase64("crc64nvme", Data);
        var wire = Encoding.ASCII.GetBytes($"f\r\nhello symposia\n\r\n0\r\nx-amz-checksum-crc64nvme:{crc}\r\n\r\n");
        var ctx = Request(wire, ("x-amz-content-sha256", "STREAMING-UNSIGNED-PAYLOAD-TRAILER"), ("Content-Encoding", "aws-chunked"),
            ("x-amz-trailer", "x-amz-checksum-crc64nvme"), ("x-amz-decoded-content-length", "15"));
        var stream = UploadBody.Open(ctx);
        Assert.Equal(Data, await Drain(stream));
        Assert.Equal(crc, stream.Computed["crc64nvme"]);
    }

    [Fact]
    public async Task WrongContentMd5_IsBadDigest()
    {
        var ctx = Request(Data, ("Content-MD5", Convert.ToBase64String(MD5.HashData("nope"u8))));
        Assert.Equal("BadDigest", (await Assert.ThrowsAsync<S3RequestRejectedException>(() => Drain(UploadBody.Open(ctx)))).Code);
    }

    [Fact]
    public async Task WrongHexPayloadHash_IsXAmzContentSHA256Mismatch()
    {
        var ctx = Request(Data, ("x-amz-content-sha256", Convert.ToHexStringLower(SHA256.HashData("nope"u8))));
        Assert.Equal("XAmzContentSHA256Mismatch", (await Assert.ThrowsAsync<S3RequestRejectedException>(() => Drain(UploadBody.Open(ctx)))).Code);
    }

    [Fact]
    public void UnsupportedChecksumAlgorithm_IsRejectedBeforeReading() =>
        Assert.Equal("InvalidRequest", Assert.Throws<S3RequestRejectedException>(() => UploadBody.Open(Request(Data, ("x-amz-checksum-foo", "AAAA")))).Code);

    [Fact]
    public async Task UnsignedPayloadWithoutChecksums_IsUnchanged() =>
        Assert.Equal(Data, await Drain(UploadBody.Open(Request(Data, ("x-amz-content-sha256", "UNSIGNED-PAYLOAD")))));
}
