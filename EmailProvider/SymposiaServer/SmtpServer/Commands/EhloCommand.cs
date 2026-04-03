using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;


// ────────────────────────────────────────────────
// Concrete command implementations
// ────────────────────────────────────────────────

public class EhloCommand : SmtpCommandBase
{
    private readonly SmtpServerOptions _options;
    private readonly ILogger<EhloCommand> _logger;

    public EhloCommand(SmtpServerOptions options, ILogger<EhloCommand> logger)
    {
        _options = options;
        _logger = logger;
    }

    public override string[] SupportedVerbs => new[] { "EHLO", "HELO" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        session.HasGreeted = true;
        session.ResetTransaction();

        _logger.LogDebug("Advertising SMTP capabilities for server {ServerName}", connection.ServerName);

        await connection.WriteLineAsync($"250-{connection.ServerName} Hello");
        await connection.WriteLineAsync("250-8BITMIME");
        await connection.WriteLineAsync("250-SIZE 10485760");

        if (connection.CanStartTls && !connection.IsTlsActive)
        {
            await connection.WriteLineAsync("250-STARTTLS");
        }

        if (_options.IsAuthConfigured)
        {
            await connection.WriteLineAsync("250-AUTH PLAIN LOGIN");
        }

        await connection.WriteLineAsync("250 HELP");
    }
}
