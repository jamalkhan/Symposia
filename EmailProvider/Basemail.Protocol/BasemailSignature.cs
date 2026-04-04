using System.Security.Cryptography;

namespace Basemail.Protocol;

public static class BasemailSignature
{
    public static string Sign(byte[] canonicalBytes, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var signature = ecdsa.SignData(canonicalBytes, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    public static bool Verify(byte[] canonicalBytes, string publicKeyPem, string signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            var signatureBytes = Convert.FromBase64String(signature);
            return ecdsa.VerifyData(canonicalBytes, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}
