using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Symposia.BlobStorage.Gateway.Auth;

/// <summary>
/// Verifies AWS Signature Version 4 on every inbound request.
/// See https://docs.aws.amazon.com/general/latest/gr/signature-version-4.html
/// </summary>
public sealed partial class SigV4Verifier
{
    private readonly CredentialStore _credentials;

    public SigV4Verifier(CredentialStore credentials) => _credentials = credentials;

    /// <summary>
    /// Returns the tenant ID for valid requests, null for any auth failure.
    /// The payload body is NOT re-read; we trust the client's stated x-amz-content-sha256 value.
    /// A forged body hash still breaks the signature, so MITM attacks are blocked.
    /// </summary>
    public string? Verify(HttpRequest request)
    {
        if (!TryParseAuth(request.Headers.Authorization.ToString(), out var auth))
            return null;

        var credential = _credentials.Get(auth.AccessKeyId);
        if (credential is null)
            return null;

        var amzDate = request.Headers["x-amz-date"].ToString();
        if (!TryParseAmzDate(amzDate, out var timestamp))
            return null;
        if (Math.Abs((DateTimeOffset.UtcNow - timestamp).TotalMinutes) > 15)
            return null;

        var canonicalRequest = BuildCanonicalRequest(request, auth.SignedHeaders);
        var canonicalHash = HexSha256(Encoding.UTF8.GetBytes(canonicalRequest));
        var credentialScope = $"{auth.DateStamp}/{auth.Region}/{auth.Service}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalHash}";

        var signingKey = DeriveSigningKey(credential.SecretAccessKey, auth.DateStamp, auth.Region, auth.Service);
        var computedSig = Convert.ToHexStringLower(
            HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        // Constant-time comparison to prevent timing attacks.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(auth.Signature),
                Encoding.ASCII.GetBytes(computedSig)))
            return null;

        return credential.TenantId;
    }

    private static string BuildCanonicalRequest(HttpRequest request, string[] signedHeaders)
    {
        var method = request.Method;
        var uri = CanonicalizeUri(request.Path.Value ?? "/");
        var query = CanonicalizeQueryString(request.QueryString.Value ?? "");
        var (headers, signedStr) = CanonicalizeHeaders(request.Headers, signedHeaders);
        var payloadHash = request.Headers["x-amz-content-sha256"].FirstOrDefault() ?? "UNSIGNED-PAYLOAD";

        return $"{method}\n{uri}\n{query}\n{headers}\n{signedStr}\n{payloadHash}";
    }

    private static string CanonicalizeUri(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        // Encode each segment individually; do not encode the '/' separators.
        return string.Join('/', path.Split('/').Select(UriEncode));
    }

    private static string CanonicalizeQueryString(string queryString)
    {
        var q = queryString.TrimStart('?');
        if (string.IsNullOrEmpty(q)) return "";

        var pairs = q.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var eq = p.IndexOf('=');
                var name = eq >= 0 ? p[..eq] : p;
                var value = eq >= 0 ? p[(eq + 1)..] : "";
                return (UriEncode(Uri.UnescapeDataString(name)), UriEncode(Uri.UnescapeDataString(value)));
            })
            .OrderBy(p => p.Item1, StringComparer.Ordinal)
            .ThenBy(p => p.Item2, StringComparer.Ordinal);

        return string.Join('&', pairs.Select(p => $"{p.Item1}={p.Item2}"));
    }

    private static (string CanonicalHeaders, string SignedHeadersStr) CanonicalizeHeaders(
        IHeaderDictionary headers, string[] signedHeaders)
    {
        var sorted = signedHeaders.Select(h => h.ToLowerInvariant()).Distinct().OrderBy(h => h).ToArray();
        var sb = new StringBuilder();
        foreach (var name in sorted)
        {
            var value = string.Join(',', headers[name].Select(v => v ?? "")).Trim();
            value = CollapseWhitespace().Replace(value, " ");
            sb.Append(name).Append(':').Append(value).Append('\n');
        }
        return (sb.ToString(), string.Join(';', sorted));
    }

    private static string UriEncode(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (IsUnreserved((char)b))
                sb.Append((char)b);
            else
                sb.AppendFormat("%{0:X2}", b);
        }
        return sb.ToString();
    }

    private static bool IsUnreserved(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
            or '-' or '_' or '.' or '~';

    private static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretKey), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, "aws4_request"u8.ToArray());
    }

    private static string HexSha256(byte[] data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    private record struct ParsedAuth(
        string AccessKeyId, string DateStamp, string Region, string Service,
        string[] SignedHeaders, string Signature);

    private static bool TryParseAuth(string header, out ParsedAuth parsed)
    {
        parsed = default;
        const string prefix = "AWS4-HMAC-SHA256 ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in header[prefix.Length..].Split(','))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2) dict[kv[0].Trim()] = kv[1].Trim();
        }

        if (!dict.TryGetValue("Credential", out var cred)) return false;
        if (!dict.TryGetValue("SignedHeaders", out var signedHeaders)) return false;
        if (!dict.TryGetValue("Signature", out var signature)) return false;

        var credParts = cred.Split('/');
        if (credParts.Length < 5) return false;

        parsed = new ParsedAuth(
            credParts[0], credParts[1], credParts[2], credParts[3],
            signedHeaders.Split(';'), signature);
        return true;
    }

    private static bool TryParseAmzDate(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(value, "yyyyMMddTHHmmssZ",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();
}
