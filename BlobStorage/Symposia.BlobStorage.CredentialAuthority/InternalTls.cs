using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain;

namespace Symposia.BlobStorage.CredentialAuthority;

/// <summary>
/// The internal API's mutual TLS (#245 FR6): a dedicated Kestrel listener for <c>/internal/*</c> and Watch whose TLS
/// handshake checks the gateway's client certificate against the fingerprint allowlist, plus a per-request guard so a
/// misconfiguration that serves <c>/internal</c> elsewhere can never bypass it. The service token stays as the second factor.
/// </summary>
public sealed class InternalTls(IOptionsMonitor<AuthorityOptions> options, IHostEnvironment env, ILogger<InternalTls> logger)
{
    private static readonly Meter Meter = new("Symposia.BlobStorage.CredentialAuthority");
    private static readonly Counter<long> WithoutCert = Meter.CreateCounter<long>("authority_internal_requests_without_cert_total");

    /// <summary>Requests accepted without a client certificate (Permissive), for tests and the rollout check.</summary>
    public long RequestsWithoutCertificate => Interlocked.Read(ref _withoutCert);

    private long _withoutCert;

    public InternalTlsMode Mode => Resolve(options.CurrentValue.InternalTls, env);

    public static InternalTlsMode Resolve(InternalTlsOptions o, IHostEnvironment env) =>
        o.Mode ?? (env.IsDevelopment() || string.IsNullOrEmpty(o.CertificatePath) ? InternalTlsMode.Off : InternalTlsMode.Permissive);

    /// <summary>Adds the internal TLS listener to Kestrel (Permissive/Enforced); the default listeners keep serving /v1 and /health.</summary>
    public static void Configure(WebApplicationBuilder builder)
    {
        var o = builder.Configuration.GetSection("CredentialAuthority:InternalTls").Get<InternalTlsOptions>() ?? new InternalTlsOptions();
        var mode = Resolve(o, builder.Environment);
        if (mode == InternalTlsMode.Off)
        {
            return;
        }

        if (string.IsNullOrEmpty(o.CertificatePath))
        {
            throw new InvalidOperationException($"CredentialAuthority:InternalTls:Mode is {mode} but no CertificatePath is configured.");
        }

        var certificate = TlsPin.Load(o.CertificatePath, o.KeyPath, o.CertificatePassword);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Explicit listeners make Kestrel ignore ASPNETCORE_URLS, so /v1 and /health get one of their own unless
            // Kestrel:Endpoints already declares them.
            var endpoints = builder.Configuration.GetSection("Kestrel:Endpoints");
            if (endpoints.GetChildren().Any())
            {
                kestrel.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false);
            }
            else
            {
                kestrel.ListenAnyIP(o.OperatorPort);
            }

            kestrel.ListenAnyIP(o.Port, listen =>
            {
                listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    https.ClientCertificateMode = mode == InternalTlsMode.Enforced ? ClientCertificateMode.RequireCertificate : ClientCertificateMode.AllowCertificate;
                    // Pins, not a CA: chain errors on self-signed gateway certificates are expected. The allowlist is
                    // re-read per handshake, so adding or removing a gateway needs no restart.
                    https.ClientCertificateValidation = (cert, _, _) =>
                        TlsPin.Matches(cert, CurrentPins(builder), DateTimeOffset.UtcNow);
                });
            });
        });
    }

    /// <summary>The allowlist as configured right now (configuration reloads, so no restart is needed).</summary>
    private static IEnumerable<string> CurrentPins(WebApplicationBuilder builder) =>
        builder.Configuration.GetSection("CredentialAuthority:InternalTls:ClientFingerprints").Get<string[]>() ?? [];

    /// <summary>Logs the mode once the app is built.</summary>
    public void Attach()
    {
        if (Mode == InternalTlsMode.Off)
        {
            logger.LogWarning("Internal API mTLS is OFF: /internal/* and Watch are protected only by the service token. Use only in development (#245).");
        }
    }

    /// <summary>
    /// Whether a request may use the internal API: when TLS is on, it must have arrived on the internal listener and
    /// carry an allowlisted certificate (Enforced), or carry none, counted (Permissive). Null when allowed, else why not.
    /// </summary>
    public string? Refuse(HttpContext http)
    {
        var mode = Mode;
        if (mode == InternalTlsMode.Off)
        {
            return null;
        }

        var o = options.CurrentValue.InternalTls;
        if (http.Connection.LocalPort != o.Port)
        {
            return "the internal API is served only on the internal TLS listener";
        }

        var certificate = http.Connection.ClientCertificate;
        if (certificate is null)
        {
            if (mode == InternalTlsMode.Enforced)
            {
                return "a client certificate is required";
            }

            Interlocked.Increment(ref _withoutCert);
            WithoutCert.Add(1, new KeyValuePair<string, object?>("endpoint", http.Request.Path.Value));
            return null;
        }

        return TlsPin.Matches(certificate, o.ClientFingerprints, DateTimeOffset.UtcNow) ? null : "client certificate not allowlisted";
    }
}
