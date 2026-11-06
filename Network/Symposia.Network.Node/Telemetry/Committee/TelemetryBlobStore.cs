using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nethereum.Hex.HexConvertors.Extensions;

namespace Symposia.Network.Node.Telemetry.Committee;

/// <summary>
/// Content-addressed storage for raw checkpoint batches (issue #158). The address is
/// <see cref="TelemetryBatchCodec.Address"/> (keccak256 of the bytes); readers always re-hash.
/// </summary>
public interface ITelemetryBlobStore
{
    Task PutAsync(byte[] address, byte[] content, CancellationToken ct);

    /// <summary>The bytes stored at <paramref name="address"/>, or null if absent. Never returns bytes that don't hash to the address.</summary>
    Task<byte[]?> GetAsync(byte[] address, CancellationToken ct);
}

/// <summary>Blob-store settings: a Symposia blob gateway bucket with an S3 access key.</summary>
public sealed class TelemetryBlobOptions
{
    /// <summary>Base URL of the S3-compatible blob gateway (path-style), e.g. <c>http://127.0.0.1:9000</c>.</summary>
    public string Endpoint { get; set; } = "";

    public string Bucket { get; set; } = "telemetry";

    public string Region { get; set; } = "us-east-1";

    public string AccessKeyId { get; set; } = "";

    public string SecretAccessKey { get; set; } = "";

    /// <summary>Local content-addressed cache; batches this member proposed or endorsed stay readable if the gateway is down.</summary>
    public string CacheDirectory { get; set; } = "";
}

/// <summary>
/// Minimal S3 client for the Symposia blob gateway: PUT and GET one object, signed with AWS SigV4
/// over the full payload hash. Objects live at <c>{bucket}/telemetry/{0xaddress}</c>.
/// </summary>
public sealed class S3TelemetryBlobStore(HttpClient http, TelemetryBlobOptions options) : ITelemetryBlobStore
{
    public async Task PutAsync(byte[] address, byte[] content, CancellationToken ct)
    {
        using var request = Signed(HttpMethod.Put, address, content);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Blob PUT {address.ToHex(true)} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    public async Task<byte[]?> GetAsync(byte[] address, CancellationToken ct)
    {
        using var request = Signed(HttpMethod.Get, address, []);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return TelemetryBatchCodec.Address(bytes).AsSpan().SequenceEqual(address) ? bytes : null;
    }

    private HttpRequestMessage Signed(HttpMethod method, byte[] address, byte[] body)
    {
        var path = $"/{options.Bucket}/telemetry/{address.ToHex(true)}";
        var uri = new Uri(new Uri(options.Endpoint.TrimEnd('/') + "/"), path.TrimStart('/'));
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dateStamp = now.ToString("yyyyMMdd");
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(body));
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        var canonical = $"{method.Method}\n{uri.AbsolutePath}\n\nhost:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n\nhost;x-amz-content-sha256;x-amz-date\n{payloadHash}";
        var scope = $"{dateStamp}/{options.Region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + options.SecretAccessKey), dateStamp), options.Region), "s3"), "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(key, toSign));

        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={options.AccessKeyId}/{scope}, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={signature}");
        if (method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent(body);
        }

        return request;
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}

/// <summary>
/// Read-through, write-through local cache in front of another store. Content-addressed and
/// immutable, so there's nothing to invalidate. With no backing store it's a pure local store
/// (single-host dev, tests).
/// </summary>
public sealed class CachingTelemetryBlobStore(string? directory, ITelemetryBlobStore? backing) : ITelemetryBlobStore
{
    private const int MaxInMemory = 256; // ~2 epochs of windows; older batches come back from disk or the backing store

    private readonly ConcurrentDictionary<string, byte[]> _memory = new();

    public async Task PutAsync(byte[] address, byte[] content, CancellationToken ct)
    {
        if (!TelemetryBatchCodec.Address(content).AsSpan().SequenceEqual(address))
        {
            throw new ArgumentException("Content doesn't hash to its address.", nameof(content));
        }

        await StoreLocalAsync(address, content, ct);
        if (backing is not null)
        {
            await backing.PutAsync(address, content, ct);
        }
    }

    public async Task<byte[]?> GetAsync(byte[] address, CancellationToken ct)
    {
        var key = address.ToHex();
        if (_memory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (directory is not null && File.Exists(Path.Combine(directory, key)))
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, key), ct);
            if (TelemetryBatchCodec.Address(bytes).AsSpan().SequenceEqual(address))
            {
                _memory[key] = bytes;
                return bytes;
            }
        }

        var fetched = backing is null ? null : await backing.GetAsync(address, ct);
        if (fetched is not null)
        {
            await StoreLocalAsync(address, fetched, ct);
        }

        return fetched;
    }

    private async Task StoreLocalAsync(byte[] address, byte[] content, CancellationToken ct)
    {
        var key = address.ToHex();
        if (_memory.Count >= MaxInMemory)
        {
            _memory.Clear();
        }

        _memory[key] = content;
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, key), content, ct);
        }
    }
}
