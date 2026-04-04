namespace NativeSmtpReceiver;

public sealed class DashboardWebOptions
{
    public int HttpPort { get; init; } = 5080;
    public int HttpsPort { get; init; } = 5443;
    public string? TlsCertificatePath { get; init; }
    public string? TlsCertificatePassword { get; init; }

    public static DashboardWebOptions LoadFromEnvironment()
    {
        var certificatePath = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SYMPOSIA_HTTP_TLS_CERT_PATH"),
            Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_TLS_CERT_PATH"));
        var certificatePassword = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SYMPOSIA_HTTP_TLS_CERT_PASSWORD"),
            Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_TLS_CERT_PASSWORD"));

        return new DashboardWebOptions
        {
            HttpPort = ParsePort("SYMPOSIA_HTTP_PORT", 5080),
            HttpsPort = ParsePort("SYMPOSIA_HTTPS_PORT", 5443),
            TlsCertificatePath = PathResolution.ResolveOptionalPath(certificatePath),
            TlsCertificatePassword = certificatePassword
        };
    }

    public bool TryGetHttpsCertificate(out string certificatePath, out string? certificatePassword)
    {
        if (!string.IsNullOrWhiteSpace(TlsCertificatePath) && File.Exists(TlsCertificatePath))
        {
            certificatePath = TlsCertificatePath;
            certificatePassword = TlsCertificatePassword;
            return true;
        }

        certificatePath = string.Empty;
        certificatePassword = null;
        return false;
    }

    private static int ParsePort(string environmentVariable, int defaultValue)
    {
        var rawValue = Environment.GetEnvironmentVariable(environmentVariable);
        if (int.TryParse(rawValue, out var value) && value >= 0 && value <= 65535)
        {
            return value;
        }

        return defaultValue;
    }
    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
