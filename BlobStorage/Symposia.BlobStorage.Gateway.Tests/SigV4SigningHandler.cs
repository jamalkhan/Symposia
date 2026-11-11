using System.Security.Cryptography;
using System.Text;
using Symposia.BlobStorage.Gateway.Auth;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>Signs requests with AWS Signature V4 (header form, UNSIGNED-PAYLOAD) for tests of the /v1 API.</summary>
internal sealed class SigV4SigningHandler(HttpMessageHandler inner, string accessKey, string secretKey, string region = "us-east-1")
    : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dateStamp = now.ToString("yyyyMMdd");
        var uri = request.RequestUri!;
        request.Headers.Remove("x-amz-date");
        request.Headers.Add("x-amz-date", amzDate);
        request.Headers.Add("x-amz-content-sha256", "UNSIGNED-PAYLOAD");
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        var path = string.Join('/', Uri.UnescapeDataString(uri.AbsolutePath).Split('/').Select(Encode));
        var query = string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => { var eq = p.IndexOf('='); return (Encode(Uri.UnescapeDataString(eq >= 0 ? p[..eq] : p)), Encode(Uri.UnescapeDataString(eq >= 0 ? p[(eq + 1)..] : ""))); })
            .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal)
            .Select(p => $"{p.Item1}={p.Item2}"));
        const string signed = "host;x-amz-content-sha256;x-amz-date";
        var canonical = $"{request.Method.Method}\n{path}\n{query}\nhost:{host}\nx-amz-content-sha256:UNSIGNED-PAYLOAD\nx-amz-date:{amzDate}\n\n{signed}\nUNSIGNED-PAYLOAD";
        var scope = $"{dateStamp}/{region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var signature = SigV4Verifier.SignHex(SigV4Verifier.SigningKey(secretKey, dateStamp, region, "s3"), toSign);
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signed}, Signature={signature}");
        return base.SendAsync(request, ct);
    }

    private static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append($"%{b:X2}");
        }

        return sb.ToString();
    }
}
