namespace InboxWeb;

public sealed record InboxWebOptions
{
    public int HttpPort { get; init; } = 5190;
    public int HttpsPort { get; init; } = 5545;
    public string HostingConfigPath { get; init; } = string.Empty;
    public string AccountStorePath { get; init; } = string.Empty;
    public string? TlsCertificatePath { get; init; }
    public string? TlsCertificatePassword { get; init; }

    public static InboxWebOptions LoadFromEnvironment()
    {
        var certificatePath = ResolveOptionalPath(
            Environment.GetEnvironmentVariable("SYMPOSIA_INBOX_TLS_CERT_PATH")
            ?? Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_TLS_CERT_PATH"));

        return new InboxWebOptions
        {
            HttpPort = ResolveInt("SYMPOSIA_INBOX_HTTP_PORT", 5190),
            HttpsPort = ResolveInt("SYMPOSIA_INBOX_HTTPS_PORT", 5545),
            HostingConfigPath = ResolveHostingConfigPath(),
            AccountStorePath = ResolveAccountStorePath(),
            TlsCertificatePath = certificatePath,
            TlsCertificatePassword = Environment.GetEnvironmentVariable("SYMPOSIA_INBOX_TLS_CERT_PASSWORD")
                ?? Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_TLS_CERT_PASSWORD")
        };
    }

    public bool TryGetHttpsCertificate(out string certificatePath, out string certificatePassword)
    {
        if (!string.IsNullOrWhiteSpace(TlsCertificatePath) &&
            File.Exists(TlsCertificatePath) &&
            !string.IsNullOrWhiteSpace(TlsCertificatePassword))
        {
            certificatePath = TlsCertificatePath;
            certificatePassword = TlsCertificatePassword;
            return true;
        }

        certificatePath = string.Empty;
        certificatePassword = string.Empty;
        return false;
    }

    private static int ResolveInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : fallback;
    }

    private static string ResolveHostingConfigPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_HOSTING_CONFIG")
            ?? Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_RECIPIENT_CONFIG");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Path.Combine(AppContext.BaseDirectory, "Config", "mailboxes.json");
        }

        return ResolvePath(configuredPath);
    }

    private static string ResolveAccountStorePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SYMPOSIA_INBOX_ACCOUNT_STORE_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Path.Combine(AppContext.BaseDirectory, "Config", "accounts.json");
        }

        return ResolvePath(configuredPath);
    }

    private static string? ResolveOptionalPath(string? configuredPath)
    {
        return string.IsNullOrWhiteSpace(configuredPath)
            ? null
            : ResolvePath(configuredPath);
    }

    private static string ResolvePath(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, Environment.CurrentDirectory);
    }
}
