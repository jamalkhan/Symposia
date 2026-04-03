using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            options.SingleLine = true;
            options.IncludeScopes = false;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var smtpOptions = SmtpServerOptions.LoadFromEnvironment();
        var webOptions = DashboardWebOptions.LoadFromEnvironment();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(webOptions.HttpPort);

            if (webOptions.TryGetHttpsCertificate(out var certificatePath, out var certificatePassword))
            {
                options.ListenAnyIP(webOptions.HttpsPort, listenOptions =>
                {
                    listenOptions.UseHttps(certificatePath, certificatePassword);
                });
            }
        });

        builder.Services.AddSingleton(smtpOptions);
        builder.Services.AddSingleton(webOptions);
        builder.Services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<HostingDirectory>>();
            var directory = HostingDirectory.LoadFromEnvironment();
            logger.LogInformation(
                "Loaded hosting configuration with {DomainCount} domains and {ProviderCount} storage providers",
                directory.DomainCount,
                directory.StorageProviderCount);
            return directory;
        });

        builder.Services.AddHttpLogging(static options =>
        {
            options.LoggingFields = HttpLoggingFields.RequestPath | HttpLoggingFields.ResponseStatusCode;
        });
        builder.Services.AddControllers();

        builder.Services.AddSingleton<SmtpConnectionGuard>();
        builder.Services.AddSingleton<MailboxStorageProviderCatalog>();
        builder.Services.AddSingleton<MailboxDeliveryService>();
        builder.Services.AddSingleton<MailboxReadService>();
        builder.Services.AddSingleton<MailboxRetryQueueService>();
        builder.Services.AddSingleton<DashboardSummaryService>();
        builder.Services.AddSingleton<ISmtpCommand, EhloCommand>();
        builder.Services.AddSingleton<ISmtpCommand, HelpCommand>();
        builder.Services.AddSingleton<ISmtpCommand, MailFromCommand>();
        builder.Services.AddSingleton<ISmtpCommand, RcptToCommand>();
        builder.Services.AddSingleton<ISmtpCommand, DataCommand>();
        builder.Services.AddSingleton<ISmtpCommand, StartTlsCommand>();
        builder.Services.AddSingleton<ISmtpCommand, AuthCommand>();
        builder.Services.AddSingleton<ISmtpCommand, VrfyCommand>();
        builder.Services.AddSingleton<ISmtpCommand, ExpnCommand>();
        builder.Services.AddSingleton<ISmtpCommand, QuitCommand>();
        builder.Services.AddSingleton<ISmtpCommand, RsetCommand>();
        builder.Services.AddSingleton<ISmtpCommand, NoopCommand>();
        builder.Services.AddSingleton<UnknownCommand>();
        builder.Services.AddSingleton<DataLineCommand>();
        builder.Services.AddSingleton<SmtpCommandRegistry>();
        builder.Services.AddTransient<SmtpSessionHandler>();
        builder.Services.AddHostedService<SmtpServerHostedService>();
        builder.Services.AddHostedService<MailboxRetryWorker>();

        var app = builder.Build();
        var webRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var webRootProvider = new PhysicalFileProvider(webRootPath);

        _ = app.Services.GetRequiredService<HostingDirectory>();
        _ = app.Services.GetRequiredService<SmtpCommandRegistry>();

        app.UseHttpLogging();
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = webRootProvider
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = webRootProvider
        });
        app.MapControllers();

        app.Logger.LogInformation("Dashboard HTTP endpoint listening on port {Port}", webOptions.HttpPort);
        if (webOptions.TryGetHttpsCertificate(out _, out _))
        {
            app.Logger.LogInformation("Dashboard HTTPS endpoint listening on port {Port}", webOptions.HttpsPort);
        }
        else
        {
            app.Logger.LogInformation("Dashboard HTTPS endpoint is disabled because no TLS certificate is configured");
        }

        await app.RunAsync();
    }
}
