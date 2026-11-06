using System.Globalization;
using System.Text;

namespace Symposia.BlobStorage.Gateway.S3;

/// <summary>
/// Decodes an <c>aws-chunked</c> request body to the object's bytes. S3 clients use it for
/// streaming uploads (<c>x-amz-content-sha256: STREAMING-…</c>); recent AWS CLI / SDK versions
/// send it by default over HTTPS with a trailing checksum. Wire format:
/// <code>
/// &lt;hex-size&gt;[;chunk-signature=&lt;sig&gt;]\r\n&lt;data&gt;\r\n … 0[;…]\r\n[&lt;trailer&gt;:&lt;value&gt;\r\n]*\r\n
/// </code>
/// Without this the framing and trailers were stored as part of the object (issue #135).
/// With a <see cref="ChunkSignatureChain"/> every chunk signature (and the trailer signature) is
/// verified (issue #139). Trailers, including trailing checksums, are exposed in <see cref="Trailers"/>
/// once the body has been read; <see cref="ChecksumVerifyingStream"/> checks them.
/// </summary>
public sealed class AwsChunkedStream(Stream inner, long? decodedLength, ChunkSignatureChain? chain = null) : Stream
{
    private const int MaxHeaderLine = 4096;

    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _bufferStart;
    private int _bufferEnd;
    private long _chunkRemaining;
    private bool _done;
    private long _produced;
    private string? _chunkSignature;
    private System.Security.Cryptography.IncrementalHash? _chunkHash;
    private readonly Dictionary<string, string> _trailers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Trailing headers (e.g. <c>x-amz-checksum-crc64nvme</c>), complete once the body is fully read.</summary>
    public IReadOnlyDictionary<string, string> Trailers => _trailers;

    /// <summary>True when the request carries an aws-chunked body that must be decoded.</summary>
    public static bool Applies(HttpRequest request)
    {
        var sha = request.Headers["x-amz-content-sha256"].FirstOrDefault();
        var encoding = request.Headers.ContentEncoding.ToString();
        return (sha is not null && sha.StartsWith("STREAMING-", StringComparison.Ordinal))
            || encoding.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase);
    }

    public static long? DecodedLength(HttpRequest request) =>
        long.TryParse(request.Headers["x-amz-decoded-content-length"].FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        if (_done || destination.Length == 0)
        {
            return 0;
        }

        if (_chunkRemaining == 0)
        {
            var line = await ReadLineAsync(ct);
            var parts = line.Split(';', 2);
            var sizeText = parts[0].Trim();
            if (!long.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _chunkRemaining) || _chunkRemaining < 0)
            {
                throw new InvalidDataException($"Malformed aws-chunked chunk header '{line}'.");
            }

            _chunkSignature = parts.Length > 1 && parts[1].Trim().StartsWith("chunk-signature=", StringComparison.Ordinal)
                ? parts[1].Trim()["chunk-signature=".Length..]
                : null;
            if (chain is not null)
            {
                _chunkHash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            }

            if (_chunkRemaining == 0)
            {
                // Final chunk: its signature covers empty data; then trailers (e.g. x-amz-checksum-crc64nvme)
                // up to the blank line, with their own signature in the -TRAILER variant.
                chain?.VerifyChunk(_chunkSignature, _chunkHash!.GetHashAndReset());
                var canonicalTrailers = new StringBuilder();
                string? trailerSignature = null;
                string trailer;
                while ((trailer = await ReadLineAsync(ct)).Length > 0)
                {
                    var colon = trailer.IndexOf(':');
                    if (colon <= 0)
                    {
                        throw new InvalidDataException($"Malformed aws-chunked trailer '{trailer}'.");
                    }

                    var name = trailer[..colon].Trim();
                    var value = trailer[(colon + 1)..].Trim();
                    if (name.Equals("x-amz-trailer-signature", StringComparison.OrdinalIgnoreCase))
                    {
                        trailerSignature = value;
                        continue;
                    }

                    _trailers[name] = value;
                    canonicalTrailers.Append(name.ToLowerInvariant()).Append(':').Append(value).Append('\n');
                }

                if (chain is { Trailer: true })
                {
                    chain.VerifyTrailer(trailerSignature, canonicalTrailers.ToString());
                }

                _done = true;
                if (decodedLength is { } expected && expected != _produced)
                {
                    throw new InvalidDataException($"aws-chunked body decoded to {_produced} bytes; x-amz-decoded-content-length says {expected}.");
                }

                return 0;
            }
        }

        if (_bufferStart == _bufferEnd && !await FillAsync(ct))
        {
            throw new InvalidDataException("aws-chunked body ended inside a chunk.");
        }

        var count = (int)Math.Min(Math.Min(destination.Length, _chunkRemaining), _bufferEnd - _bufferStart);
        _buffer.AsMemory(_bufferStart, count).CopyTo(destination);
        _chunkHash?.AppendData(_buffer, _bufferStart, count);
        _bufferStart += count;
        _chunkRemaining -= count;
        _produced += count;

        if (_chunkRemaining == 0)
        {
            if ((await ReadLineAsync(ct)).Length != 0)
            {
                throw new InvalidDataException("aws-chunked chunk data was not followed by CRLF.");
            }

            chain?.VerifyChunk(_chunkSignature, _chunkHash!.GetHashAndReset());
        }

        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_bufferStart == _bufferEnd && !await FillAsync(ct))
            {
                throw new InvalidDataException("aws-chunked body ended unexpectedly.");
            }

            var b = _buffer[_bufferStart++];
            if (b == (byte)'\n')
            {
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }

                return line.ToString();
            }

            if (line.Length >= MaxHeaderLine)
            {
                throw new InvalidDataException("aws-chunked header line is too long.");
            }

            line.Append((char)b);
        }
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        _bufferStart = 0;
        _bufferEnd = await inner.ReadAsync(_buffer, ct);
        return _bufferEnd > 0;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _produced; set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Verifies the signature chain of a SigV4 streaming upload (issue #139): each chunk is signed over
/// the previous signature and its data hash, starting from the request's seed signature; the
/// -TRAILER variant also signs the trailing headers. Per the AWS "Signature Calculation: Transfer
/// Payload in Multiple Chunks" specification.
/// </summary>
public sealed class ChunkSignatureChain(Auth.SigV4Context context, bool trailer)
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private string _previous = context.SeedSignature;

    public bool Trailer { get; } = trailer;

    public void VerifyChunk(string? signature, byte[] dataSha256)
    {
        var expected = Auth.SigV4Verifier.SignHex(context.SigningKey,
            $"AWS4-HMAC-SHA256-PAYLOAD\n{context.AmzDate}\n{context.Scope}\n{_previous}\n{EmptySha256}\n{Convert.ToHexStringLower(dataSha256)}");
        Check(signature, expected, "chunk");
    }

    public void VerifyTrailer(string? signature, string canonicalTrailers)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTrailers)));
        var expected = Auth.SigV4Verifier.SignHex(context.SigningKey,
            $"AWS4-HMAC-SHA256-TRAILER\n{context.AmzDate}\n{context.Scope}\n{_previous}\n{hash}");
        Check(signature, expected, "trailer");
    }

    private void Check(string? signature, string expected, string what)
    {
        if (signature is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(expected)))
        {
            throw new S3RequestRejectedException(403, "SignatureDoesNotMatch",
                $"The {what} signature does not match; the upload was altered or signed with a different key.");
        }

        _previous = expected;
    }
}
