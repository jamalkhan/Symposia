using Symposia.BlobStorage.Gateway.Auth;

namespace Symposia.BlobStorage.Gateway.S3;

/// <summary>One declared integrity check: an algorithm, how to get the expected value, and the S3 error on mismatch.</summary>
public sealed record ChecksumExpectation(string Algorithm, Func<string?> Expected, bool Hex, string ErrorCode, string Source);

/// <summary>
/// Hashes the body as it streams and, when it ends, compares each declared checksum (issue #139).
/// On a mismatch it throws before the caller sees end-of-stream, so <see cref="Quorum.QuorumWriter"/>
/// cancels the node writes and nothing is committed.
/// </summary>
public sealed class ChecksumVerifyingStream(Stream inner, IReadOnlyList<ChecksumExpectation> expectations) : Stream
{
    private readonly List<(ChecksumExpectation Expectation, IChecksum Checksum)> _checks =
        expectations.Select(e => (e, Checksums.Create(e.Algorithm)!)).ToList();

    private bool _verified;

    /// <summary>Computed values (base64) by algorithm, available after the body is read; echoed in responses.</summary>
    public Dictionary<string, string> Computed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        if (read > 0)
        {
            foreach (var (_, checksum) in _checks)
            {
                checksum.Append(buffer.Span[..read]);
            }

            return read;
        }

        if (!_verified)
        {
            _verified = true;
            Verify();
        }

        return 0;
    }

    private void Verify()
    {
        foreach (var (expectation, checksum) in _checks)
        {
            var result = checksum.Result();
            Computed[expectation.Algorithm] = Convert.ToBase64String(result);
            var expected = expectation.Expected();
            if (expected is null)
            {
                throw new S3RequestRejectedException(400, "InvalidRequest", $"{expectation.Source} was declared but not sent.");
            }

            var matches = expectation.Hex
                ? string.Equals(Convert.ToHexStringLower(result), expected, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Convert.ToBase64String(result), expected, StringComparison.Ordinal);
            if (!matches)
            {
                throw new S3RequestRejectedException(400, expectation.ErrorCode,
                    $"The {expectation.Algorithm} checksum ({expectation.Source}) does not match the uploaded data.");
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Builds the object-bytes stream for an upload (PUT object, UploadPart): decodes aws-chunked
/// framing (#135), verifies chunk signatures and every declared checksum (#139). Declaration errors
/// (unsupported algorithm, SigV4a) are rejected before any byte is read.
/// </summary>
public static class UploadBody
{
    /// <summary>The body's decoded size when the request states it, so read-only or full nodes are skipped up front (#199).</summary>
    public static long? ExpectedLength(HttpRequest request) =>
        AwsChunkedStream.Applies(request) ? AwsChunkedStream.DecodedLength(request) : request.ContentLength;

    public static ChecksumVerifyingStream Open(HttpContext ctx)
    {
        var request = ctx.Request;
        var payload = request.Headers["x-amz-content-sha256"].FirstOrDefault();
        var expectations = new List<ChecksumExpectation>();

        Stream body = request.Body;
        AwsChunkedStream? chunked = null;
        if (AwsChunkedStream.Applies(request))
        {
            ChunkSignatureChain? chain = null;
            if (payload is "STREAMING-AWS4-HMAC-SHA256-PAYLOAD" or "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER")
            {
                var sigv4 = ctx.Items[nameof(SigV4Context)] as SigV4Context
                    ?? throw new S3RequestRejectedException(403, "AccessDenied", "Signed streaming upload without a verified signature.");
                chain = new ChunkSignatureChain(sigv4, payload.EndsWith("-TRAILER", StringComparison.Ordinal));
            }
            else if (payload?.StartsWith("STREAMING-AWS4-ECDSA", StringComparison.Ordinal) == true)
            {
                throw new S3RequestRejectedException(400, "NotImplemented", "SigV4a streaming signatures are not supported.");
            }

            chunked = new AwsChunkedStream(request.Body, AwsChunkedStream.DecodedLength(request), chain);
            body = chunked;
        }

        if (request.Headers.ContentMD5.FirstOrDefault() is { Length: > 0 } md5)
        {
            expectations.Add(new ChecksumExpectation("md5", () => md5, Hex: false, "BadDigest", "Content-MD5"));
        }

        if (payload is { Length: 64 } && payload.All(Uri.IsHexDigit))
        {
            expectations.Add(new ChecksumExpectation("sha256", () => payload, Hex: true, "XAmzContentSHA256Mismatch", "x-amz-content-sha256"));
        }

        foreach (var header in request.Headers.Keys.Where(k => k.StartsWith("x-amz-checksum-", StringComparison.OrdinalIgnoreCase)))
        {
            var algorithm = header["x-amz-checksum-".Length..].ToLowerInvariant();
            if (algorithm is "algorithm" or "type" or "mode")
            {
                continue;
            }

            Require(algorithm, header);
            var value = request.Headers[header].ToString();
            expectations.Add(new ChecksumExpectation(algorithm, () => value, Hex: false, "BadDigest", header));
        }

        foreach (var trailer in request.Headers["x-amz-trailer"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!trailer.StartsWith("x-amz-checksum-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (chunked is null)
            {
                throw new S3RequestRejectedException(400, "InvalidRequest", "x-amz-trailer requires an aws-chunked body.");
            }

            var algorithm = trailer["x-amz-checksum-".Length..].ToLowerInvariant();
            Require(algorithm, trailer);
            var source = chunked;
            expectations.Add(new ChecksumExpectation(algorithm,
                () => source.Trailers.TryGetValue(trailer, out var v) ? v : null, Hex: false, "BadDigest", $"trailer {trailer}"));
        }

        return new ChecksumVerifyingStream(body, expectations);
    }

    private static void Require(string algorithm, string source)
    {
        if (!Checksums.Algorithms.Contains(algorithm))
        {
            throw new S3RequestRejectedException(400, "InvalidRequest", $"Unsupported checksum algorithm in {source}.");
        }
    }
}
