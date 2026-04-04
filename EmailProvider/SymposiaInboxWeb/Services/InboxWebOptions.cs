namespace InboxWeb;

public sealed record InboxWebOptions
{
    public int HttpPort { get; init; } = 5190;
    public int HttpsPort { get; init; } = 5545;
    public string HostingConfigPath { get; init; } = string.Empty;
    public string AccountStorePath { get; init; } = string.Empty;
    public string? TlsCertificatePath { get; init; }
    public string? TlsCertificatePassword { get; init; }
    public string CsrfCookieName { get; init; } = "symposia-inbox-csrf";
    public string AuthCookieName { get; init; } = "symposia-inbox-auth";
    public int LockoutThreshold { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;
    public int PasswordResetTokenMinutes { get; init; } = 30;
    public bool ExposeResetTokens { get; init; }
    public string? OutboundRelayHost { get; init; }
    public int OutboundRelayPort { get; init; } = 25;
    public bool OutboundRelayUseSsl { get; init; }
    public string? OutboundRelayUsername { get; init; }
    public string? OutboundRelayPassword { get; init; }
    public int OutboundPollSeconds { get; init; } = 5;
    public int OutboundMaxAttempts { get; init; } = 5;

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
                ?? Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_TLS_CERT_PASSWORD"),
            LockoutThreshold = ResolveInt("SYMPOSIA_INBOX_LOCKOUT_THRESHOLD", 5),
            LockoutMinutes = ResolveInt("SYMPOSIA_INBOX_LOCKOUT_MINUTES", 15),
            PasswordResetTokenMinutes = ResolveInt("SYMPOSIA_INBOX_PASSWORD_RESET_TOKEN_MINUTES", 30),
            ExposeResetTokens = ResolveBool("SYMPOSIA_INBOX_EXPOSE_RESET_TOKENS", false),
            OutboundRelayHost = EmptyToNull(Environment.GetEnvironmentVariable("SYMPOSIA_INBOX_OUTBOUND_RELAY_HOST")),
            OutboundRelayPort = ResolveInt("SYMPOSIA_INBOX_OUTBOUND_RELAY_PORT", 25),
            OutboundRelayUseSsl = ResolveBool("SYMPOSIA_INBOX_OUTBOUND_RELAY_USE_SSL", false),
            OutboundRelayUsername = EmptyToNull(Environment.GetEnvironmentVariable("SYMPOSIA_INBOX_OUTBOUND_RELAY_USERNAME")),
            OutboundRelayPassword = EmptyToNull(Environment.GetEnvironmentVariable("SYMPOSIA_INBOX_OUTBOUND_RELAY_PASSWORD")),
            OutboundPollSeconds = ResolveInt("SYMPOSIA_INBOX_OUTBOUND_POLL_SECONDS", 5),
            OutboundMaxAttempts = ResolveInt("SYMPOSIA_INBOX_OUTBOUND_MAX_ATTEMPTS", 5)
        };
    }

    public bool HasOutboundRelay => !string.IsNullOrWhiteSpace(OutboundRelayHost);

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

    private static bool ResolveBool(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Equals("1", StringComparison.OrdinalIgnoreCase)
              || value.Equals("true", StringComparison.OrdinalIgnoreCase)
              || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string ResolveHostingConfigPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_HOSTING_CONFIG")
            ?? Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_RECIPIENT_CONFIG");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Path.Combine(AppContext.BaseDirectory, "Config", "mailboxes.json");
        }

        return PathResolution.ResolvePath(configuredPath);
    }

    private static string ResolveAccountStorePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SYMPOSIA_INBOX_ACCOUNT_STORE_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Path.Combine(AppContext.BaseDirectory, "Config", "accounts.json");
        }

        return PathResolution.ResolvePath(configuredPath);
    }

    private static string? ResolveOptionalPath(string? configuredPath)
    {
        return PathResolution.ResolveOptionalPath(configuredPath);
    }
}
