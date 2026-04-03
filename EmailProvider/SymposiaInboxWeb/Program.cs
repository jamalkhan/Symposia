using System.Security.Claims;
using InboxWeb;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpLogging;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.SingleLine = true;
    options.IncludeScopes = false;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

var inboxOptions = InboxWebOptions.LoadFromEnvironment();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(inboxOptions.HttpPort);

    if (inboxOptions.TryGetHttpsCertificate(out var certificatePath, out var certificatePassword))
    {
        options.ListenAnyIP(inboxOptions.HttpsPort, listenOptions =>
        {
            listenOptions.UseHttps(certificatePath, certificatePassword);
        });
    }
});

builder.Services.AddSingleton(inboxOptions);
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.RequestPath | HttpLoggingFields.ResponseStatusCode;
});
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "symposia-inbox-auth";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.Events.OnRedirectToAccessDenied = static context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToLogin = static context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddControllers();

builder.Services.AddSingleton<HostedMailboxRepository>();
builder.Services.AddSingleton<PasswordHashingService>();
builder.Services.AddSingleton<InboxAccountService>();
builder.Services.AddSingleton<MailboxContentStore>();

var app = builder.Build();

app.UseHttpLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Logger.LogInformation("Inbox HTTP endpoint listening on port {Port}", inboxOptions.HttpPort);
if (inboxOptions.TryGetHttpsCertificate(out _, out _))
{
    app.Logger.LogInformation("Inbox HTTPS endpoint listening on port {Port}", inboxOptions.HttpsPort);
}
else
{
    app.Logger.LogInformation("Inbox HTTPS endpoint is disabled because no TLS certificate is configured");
}

await app.RunAsync();

namespace InboxWeb
{
    internal static class ClaimsPrincipalExtensions
    {
        public static string? GetAccountId(this ClaimsPrincipal principal)
        {
            return principal.FindFirstValue(ClaimTypes.NameIdentifier);
        }
    }
}
