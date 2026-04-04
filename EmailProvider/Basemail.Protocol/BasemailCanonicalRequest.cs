using System.Security.Cryptography;
using System.Text;

namespace Basemail.Protocol;

public static class BasemailCanonicalRequest
{
    public static string Build(
        string method,
        string path,
        string timestamp,
        string nonce,
        byte[] body)
    {
        return string.Join(
            "\n",
            method.Trim().ToUpperInvariant(),
            path.Trim(),
            timestamp.Trim(),
            nonce.Trim(),
            ComputeContentHash(body));
    }

    public static string ComputeContentHash(byte[] body)
    {
        var hash = SHA256.HashData(body);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static byte[] GetCanonicalBytes(
        string method,
        string path,
        string timestamp,
        string nonce,
        byte[] body)
    {
        return Encoding.UTF8.GetBytes(Build(method, path, timestamp, nonce, body));
    }
}
