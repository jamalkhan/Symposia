using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class AuthCommand : SmtpCommandBase
{
    private readonly SmtpServerOptions _options;
    private readonly ILogger<AuthCommand> _logger;

    public AuthCommand(SmtpServerOptions options, ILogger<AuthCommand> logger)
    {
        _options = options;
        _logger = logger;
    }

    public override string[] SupportedVerbs => new[] { "AUTH" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        if (!session.HasGreeted)
        {
            await connection.WriteLineAsync("503 5.5.1 Send EHLO/HELO first");
            return;
        }

        var configuredUser = _options.AuthUsername;
        var configuredPassword = _options.AuthPassword;

        if (string.IsNullOrWhiteSpace(configuredUser) || string.IsNullOrWhiteSpace(configuredPassword))
        {
            _logger.LogWarning("AUTH requested but SMTP authentication is not configured");
            await connection.WriteLineAsync("454 4.7.0 Authentication unavailable");
            return;
        }

        if (session.IsAuthenticated)
        {
            await connection.WriteLineAsync("503 5.5.0 Already authenticated");
            return;
        }

        if (string.IsNullOrWhiteSpace(argument))
        {
            await connection.WriteLineAsync("501 5.5.4 Syntax: AUTH mechanism [initial-response]");
            return;
        }

        var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var mechanism = parts[0].ToUpperInvariant();
        var initialResponse = parts.Length > 1 ? parts[1] : null;

        switch (mechanism)
        {
            case "PLAIN":
                _logger.LogDebug("Processing AUTH PLAIN request");
                await HandlePlainAsync(initialResponse, configuredUser, configuredPassword, session, connection);
                break;
            case "LOGIN":
                _logger.LogDebug("Processing AUTH LOGIN request");
                await HandleLoginAsync(initialResponse, configuredUser, configuredPassword, session, connection);
                break;
            default:
                _logger.LogWarning("Unsupported authentication mechanism {Mechanism}", mechanism);
                await connection.WriteLineAsync("504 5.5.4 Unrecognized authentication type");
                break;
        }
    }

    private async Task HandlePlainAsync(
        string? initialResponse,
        string configuredUser,
        string configuredPassword,
        SmtpSession session,
        SmtpConnectionContext connection)
    {
        if (string.IsNullOrWhiteSpace(initialResponse))
        {
            await connection.WriteLineAsync("334 ");
            initialResponse = await connection.ReadLineAsync();
        }

        if (string.IsNullOrWhiteSpace(initialResponse) || initialResponse == "*")
        {
            await connection.WriteLineAsync("501 5.7.0 Authentication cancelled");
            return;
        }

        try
        {
            var decoded = DecodeBase64(initialResponse);
            var segments = decoded.Split('\0');
            if (segments.Length < 3)
            {
                await connection.WriteLineAsync("501 5.5.2 Invalid AUTH PLAIN payload");
                return;
            }

            await CompleteAuthenticationAsync(segments[1], segments[2], configuredUser, configuredPassword, session, connection);
        }
        catch (FormatException)
        {
            await connection.WriteLineAsync("501 5.5.2 Invalid base64 payload");
        }
    }

    private async Task HandleLoginAsync(
        string? initialResponse,
        string configuredUser,
        string configuredPassword,
        SmtpSession session,
        SmtpConnectionContext connection)
    {
        string? usernameResponse = initialResponse;
        if (string.IsNullOrWhiteSpace(usernameResponse))
        {
            await connection.WriteLineAsync("334 VXNlcm5hbWU6");
            usernameResponse = await connection.ReadLineAsync();
        }

        if (string.IsNullOrWhiteSpace(usernameResponse) || usernameResponse == "*")
        {
            await connection.WriteLineAsync("501 5.7.0 Authentication cancelled");
            return;
        }

        await connection.WriteLineAsync("334 UGFzc3dvcmQ6");
        var passwordResponse = await connection.ReadLineAsync();

        if (string.IsNullOrWhiteSpace(passwordResponse) || passwordResponse == "*")
        {
            await connection.WriteLineAsync("501 5.7.0 Authentication cancelled");
            return;
        }

        try
        {
            var username = DecodeBase64(usernameResponse);
            var password = DecodeBase64(passwordResponse);
            await CompleteAuthenticationAsync(username, password, configuredUser, configuredPassword, session, connection);
        }
        catch (FormatException)
        {
            await connection.WriteLineAsync("501 5.5.2 Invalid base64 payload");
        }
    }

    private async Task CompleteAuthenticationAsync(
        string username,
        string password,
        string configuredUser,
        string configuredPassword,
        SmtpSession session,
        SmtpConnectionContext connection)
    {
        if (!string.Equals(username, configuredUser, StringComparison.Ordinal) ||
            !string.Equals(password, configuredPassword, StringComparison.Ordinal))
        {
            _logger.LogWarning("SMTP authentication failed for user {Username}", username);
            await connection.WriteLineAsync("535 5.7.8 Authentication credentials invalid");
            return;
        }

        session.IsAuthenticated = true;
        session.AuthenticatedUser = username;
        _logger.LogInformation("SMTP authentication succeeded for user {Username}", username);
        await connection.WriteLineAsync("235 2.7.0 Authentication successful");
    }
}
