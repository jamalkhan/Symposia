using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class SmtpServerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SmtpServerOptions _options;
    private readonly SmtpConnectionGuard _connectionGuard;
    private readonly ILogger<SmtpServerHostedService> _logger;
    private TcpListener? _listener;

    public SmtpServerHostedService(
        IServiceScopeFactory scopeFactory,
        SmtpServerOptions options,
        SmtpConnectionGuard connectionGuard,
        ILogger<SmtpServerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _connectionGuard = connectionGuard;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SMTP server listener is disabled by configuration");
            return;
        }

        _listener = new TcpListener(IPAddress.Any, _options.Port);
        _listener.Start();

        _logger.LogInformation("SMTP server listening on port {Port}", _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting SMTP client");
                    continue;
                }

                if (!_connectionGuard.TryAcquire(client, out var lease, out var rejectionResponse))
                {
                    _logger.LogWarning(
                        "Rejected SMTP client {RemoteEndpoint}: {Reason}",
                        client.Client.RemoteEndPoint,
                        rejectionResponse);
                    _ = RejectClientAsync(client, rejectionResponse);
                    continue;
                }

                _ = HandleClientAsync(client, lease!, stoppingToken);
            }
        }
        finally
        {
            _listener.Stop();
            _logger.LogInformation("SMTP server listener stopped");
        }
    }

    private async Task HandleClientAsync(TcpClient client, SmtpConnectionGuard.SmtpConnectionLease lease, CancellationToken cancellationToken)
    {
        using var _ = lease;
        using var scope = _scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SmtpSessionHandler>();

        try
        {
            await handler.HandleAsync(client, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled failure while processing SMTP client");
            client.Dispose();
        }
    }

    private static async Task RejectClientAsync(TcpClient client, string rejectionResponse)
    {
        try
        {
            await using var stream = client.GetStream();
            await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync(rejectionResponse);
        }
        catch
        {
            // Best effort only.
        }
        finally
        {
            client.Dispose();
        }
    }
}
