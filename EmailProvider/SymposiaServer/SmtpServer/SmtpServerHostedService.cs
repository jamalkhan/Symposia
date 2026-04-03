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
    private readonly ILogger<SmtpServerHostedService> _logger;
    private TcpListener? _listener;

    public SmtpServerHostedService(
        IServiceScopeFactory scopeFactory,
        SmtpServerOptions options,
        ILogger<SmtpServerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        finally
        {
            _listener.Stop();
            _logger.LogInformation("SMTP server listener stopped");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
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
}
