namespace NativeSmtpReceiver;

public sealed class SmtpServerOptions
{
    public bool Enabled { get; init; } = true;
    public int Port { get; init; } = 2525;
    public string ServerName { get; init; } = "native-smtp.local";
    public string? TlsCertificatePath { get; init; }
    public string? TlsCertificatePassword { get; init; }
    public string? AuthUsername { get; init; }
    public string? AuthPassword { get; init; }
    public int MaxConcurrentConnections { get; init; } = 100;
    public int MaxConcurrentConnectionsPerIp { get; init; } = 10;
    public int MaxConnectionsPerIpPerMinute { get; init; } = 30;
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxCommandsPerSession { get; init; } = 200;
    public int MaxRecipientsPerMessage { get; init; } = 50;
    public int MaxMessageSizeBytes { get; init; } = 10 * 1024 * 1024;
    public bool AllowAuthenticatedRelay { get; init; }
    public string RetryQueueRootPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "retry-queue");
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsAuthConfigured =>
        !string.IsNullOrWhiteSpace(AuthUsername) &&
        !string.IsNullOrWhiteSpace(AuthPassword);

    public static SmtpServerOptions LoadFromEnvironment()
    {
        var configuredPort = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_PORT");

        return new SmtpServerOptions
        {
            Enabled = ParseBool("SYMPOSIA_SMTP_ENABLED", true),
            Port = int.TryParse(configuredPort, out var port) && port is > 0 and <= 65535 ? port : 2525,
            ServerName = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_SERVER_NAME") ?? "native-smtp.local",
            TlsCertificatePath = PathResolution.ResolveOptionalPath(Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_TLS_CERT_PATH")),
            TlsCertificatePassword = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_TLS_CERT_PASSWORD"),
            AuthUsername = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_AUTH_USERNAME"),
            AuthPassword = Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_AUTH_PASSWORD"),
            MaxConcurrentConnections = ParseInt("SYMPOSIA_SMTP_MAX_CONCURRENT_CONNECTIONS", 100, 1, 10000),
            MaxConcurrentConnectionsPerIp = ParseInt("SYMPOSIA_SMTP_MAX_CONCURRENT_CONNECTIONS_PER_IP", 10, 1, 1000),
            MaxConnectionsPerIpPerMinute = ParseInt("SYMPOSIA_SMTP_MAX_CONNECTIONS_PER_IP_PER_MINUTE", 30, 1, 10000),
            SessionIdleTimeout = TimeSpan.FromSeconds(ParseInt("SYMPOSIA_SMTP_SESSION_IDLE_TIMEOUT_SECONDS", 120, 5, 3600)),
            MaxCommandsPerSession = ParseInt("SYMPOSIA_SMTP_MAX_COMMANDS_PER_SESSION", 200, 10, 100000),
            MaxRecipientsPerMessage = ParseInt("SYMPOSIA_SMTP_MAX_RECIPIENTS_PER_MESSAGE", 50, 1, 1000),
            MaxMessageSizeBytes = ParseInt("SYMPOSIA_SMTP_MAX_MESSAGE_SIZE_BYTES", 10 * 1024 * 1024, 1024, 100 * 1024 * 1024),
            AllowAuthenticatedRelay = ParseBool("SYMPOSIA_SMTP_ALLOW_AUTHENTICATED_RELAY", false),
            RetryQueueRootPath = PathResolution.ResolveOptionalPath(Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_RETRY_QUEUE_ROOT"))
                ?? Path.Combine(AppContext.BaseDirectory, "retry-queue"),
            RetryInterval = TimeSpan.FromSeconds(ParseInt("SYMPOSIA_SMTP_RETRY_INTERVAL_SECONDS", 30, 5, 3600))
        };
    }

    private static int ParseInt(string name, int defaultValue, int min, int max)
    {
        var rawValue = Environment.GetEnvironmentVariable(name);
        return int.TryParse(rawValue, out var value) && value >= min && value <= max
            ? value
            : defaultValue;
    }

    private static bool ParseBool(string name, bool defaultValue)
    {
        var rawValue = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(rawValue, out var value) ? value : defaultValue;
    }
}
