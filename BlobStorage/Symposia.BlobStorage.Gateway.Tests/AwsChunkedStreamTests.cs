using System.Text;
using Symposia.BlobStorage.Gateway.S3;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// Issue #135: AWS CLI v2 uploads over HTTPS use aws-chunked framing with a trailing checksum;
/// the gateway stored the framing as object content until the body was decoded.
/// </summary>
public sealed class AwsChunkedStreamTests
{
    private static async Task<string> DecodeAsync(string wire, long? decodedLength = null, int readSize = 7)
    {
        using var stream = new AwsChunkedStream(new MemoryStream(Encoding.ASCII.GetBytes(wire)), decodedLength);
        using var output = new MemoryStream();
        var buffer = new byte[readSize];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(output.ToArray());
    }

    // The exact body `aws s3api put-object` sent to symposia.chateaukhan.com for "hello symposia\n".
    [Fact]
    public async Task UnsignedPayloadWithTrailingChecksum_DecodesToTheObjectBytes() =>
        Assert.Equal("hello symposia\n",
            await DecodeAsync("f\r\nhello symposia\n\r\n0\r\nx-amz-checksum-crc64nvme:SCHp0M1xkNc=\r\n\r\n", decodedLength: 15));

    [Fact]
    public async Task SignedChunks_AcrossSeveralChunks_Decode() =>
        Assert.Equal("abcdefghij",
            await DecodeAsync("4;chunk-signature=aaaa\r\nabcd\r\n6;chunk-signature=bbbb\r\nefghij\r\n0;chunk-signature=cccc\r\n\r\n", readSize: 3));

    [Fact]
    public async Task EmptyObject_Decodes() =>
        Assert.Equal("", await DecodeAsync("0\r\n\r\n", decodedLength: 0));

    [Fact]
    public async Task DecodedLengthMismatch_IsRejected() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => DecodeAsync("4\r\nabcd\r\n0\r\n\r\n", decodedLength: 5));

    [Fact]
    public async Task TruncatedBody_IsRejected() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => DecodeAsync("a\r\nabc"));

    [Fact]
    public async Task MalformedChunkHeader_IsRejected() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => DecodeAsync("zz\r\nabc\r\n"));
}
