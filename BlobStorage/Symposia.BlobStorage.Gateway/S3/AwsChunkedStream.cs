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
/// Chunk signatures and trailing checksums are not verified here (TLS protects transit and the
/// stored blob is content-addressed); see the PR notes.
/// </summary>
public sealed class AwsChunkedStream(Stream inner, long? decodedLength) : Stream
{
    private const int MaxHeaderLine = 4096;

    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _bufferStart;
    private int _bufferEnd;
    private long _chunkRemaining;
    private bool _done;
    private long _produced;

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
            var sizeText = line.Split(';', 2)[0].Trim();
            if (!long.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _chunkRemaining) || _chunkRemaining < 0)
            {
                throw new InvalidDataException($"Malformed aws-chunked chunk header '{line}'.");
            }

            if (_chunkRemaining == 0)
            {
                // Final chunk: consume trailers (e.g. x-amz-checksum-crc64nvme) up to the blank line.
                while ((await ReadLineAsync(ct)).Length > 0)
                {
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
        _bufferStart += count;
        _chunkRemaining -= count;
        _produced += count;

        if (_chunkRemaining == 0 && (await ReadLineAsync(ct)).Length != 0)
        {
            throw new InvalidDataException("aws-chunked chunk data was not followed by CRLF.");
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
