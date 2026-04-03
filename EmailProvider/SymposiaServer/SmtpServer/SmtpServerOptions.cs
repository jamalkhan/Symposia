namespace NativeSmtpReceiver;

public sealed class SmtpServerOptions
{
    public int Port { get; init; } = 2525;
    public string ServerName { get; init; } = "native-smtp.local";
    public string? TlsCertificatePath { get; init; }
    public string? TlsCertificatePassword { get; init; }
    public string? AuthUsername { get; init; }
    public string? AuthPassword { get; init; }

    public bool IsAuthConfigured =>
        !string.IsNullOrWhiteSpace(AuthUsername) &&
        !string.IsNullOrWhiteSpace(AuthPassword);

    public static SmtpServerOptions LoadFromEnvironment()
    {
        var configuredPort = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_PORT");

        return new SmtpServerOptions
        {
            Port = int.TryParse(configuredPort, out var port) && port is > 0 and <= 65535 ? port : 2525,
            ServerName = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_SERVER_NAME") ?? "native-smtp.local",
            TlsCertificatePath = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_TLS_CERT_PATH"),
            TlsCertificatePassword = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_TLS_CERT_PASSWORD"),
            AuthUsername = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_AUTH_USERNAME"),
            AuthPassword = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_AUTH_PASSWORD")
        };
    }
}
