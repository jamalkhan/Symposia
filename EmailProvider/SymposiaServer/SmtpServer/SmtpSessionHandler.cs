using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class SmtpSessionHandler
{
    private readonly SmtpServerOptions _options;
    private readonly SmtpCommandRegistry _commandRegistry;
    private readonly DataLineCommand _dataLineCommand;
    private readonly UnknownCommand _unknownCommand;
    private readonly ILogger<SmtpSessionHandler> _logger;

    public SmtpSessionHandler(
        SmtpServerOptions options,
        SmtpCommandRegistry commandRegistry,
        DataLineCommand dataLineCommand,
        UnknownCommand unknownCommand,
        ILogger<SmtpSessionHandler> logger)
    {
        _options = options;
        _commandRegistry = commandRegistry;
        _dataLineCommand = dataLineCommand;
        _unknownCommand = unknownCommand;
        _logger = logger;
    }

    public async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;

        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var remoteIp = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "unknown";
        _logger.LogInformation("Accepted SMTP client from {RemoteEndpoint}", remoteEndpoint);

        try
        {
            using var connection = new SmtpConnectionContext(client, _options);
            await connection.WriteLineAsync($"220 {_options.ServerName} ESMTP Ready");

            var session = new SmtpSession
            {
                RemoteIpAddress = remoteIp
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await connection.ReadLineAsync(_options.SessionIdleTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "SMTP session with {RemoteEndpoint} timed out after {TimeoutSeconds} seconds",
                        remoteEndpoint,
                        _options.SessionIdleTimeout.TotalSeconds);
                    await connection.WriteLineAsync("421 4.4.2 Idle timeout exceeded");
                    break;
                }

                if (line is null)
                {
                    _logger.LogInformation("SMTP client {RemoteEndpoint} disconnected", remoteEndpoint);
                    break;
                }

                session.CommandCount++;
                if (session.CommandCount > _options.MaxCommandsPerSession)
                {
                    _logger.LogWarning("SMTP session with {RemoteEndpoint} exceeded command limit", remoteEndpoint);
                    await connection.WriteLineAsync("421 4.7.0 Too many commands in session");
                    break;
                }

                _logger.LogDebug("SMTP client {RemoteEndpoint} sent: {Line}", remoteEndpoint, line);

                if (session.InDataMode)
                {
                    await _dataLineCommand.ExecuteAsync(line, null, session, connection);
                }
                else
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        await connection.WriteLineAsync("500 5.5.2 Syntax error, command unrecognized");
                        continue;
                    }

                    var upper = trimmed.ToUpperInvariant();
                    var verb = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    var argument = trimmed.Length > verb.Length ? trimmed[(verb.Length + 1)..].Trim() : null;
                    var command = _commandRegistry.Resolve(verb, _unknownCommand);

                    await command.ExecuteAsync(trimmed, argument, session, connection);
                }

                if (session.IsTerminated)
                {
                    _logger.LogInformation("SMTP session with {RemoteEndpoint} terminated via QUIT", remoteEndpoint);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP session with {RemoteEndpoint} failed", remoteEndpoint);
            throw;
        }
        finally
        {
            client.Dispose();
        }
    }
}
