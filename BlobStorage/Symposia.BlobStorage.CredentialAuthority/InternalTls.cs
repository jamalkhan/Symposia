using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using Symposia.Security.TlsPinning;

namespace Symposia.BlobStorage.CredentialAuthority;

/// <summary>The internal listener as bound at startup: the mode and port can't change without a restart (review: Carl #3).</summary>
public sealed record InternalTlsBinding(InternalTlsMode Mode, int Port);

/// <summary>
/// The internal API's mutual TLS (#245 FR6): a dedicated Kestrel listener for <c>/internal/*</c> and Watch whose TLS
/// handshake checks the gateway's client certificate against the fingerprint allowlist, plus a per-request guard so a
/// misconfiguration that serves <c>/internal</c> elsewhere can never bypass it. The service token stays as the second factor.
/// </summary>
public sealed class InternalTls(InternalTlsBinding binding, IOptionsMonitor<AuthorityOptions> options, ILogger<InternalTls> logger, IMeterFactory meters)
{
    private readonly Counter<long> _withoutCert = meters.Create("Symposia.BlobStorage.CredentialAuthority")
        .CreateCounter<long>("authority_internal_requests_without_cert_total");

    private long _withoutCertCount;

    /// <summary>Requests accepted without a client certificate (Permissive), for tests and the rollout check.</summary>
    public long RequestsWithoutCertificate => Interlocked.Read(ref _withoutCertCount);

    public InternalTlsMode Mode => binding.Mode;

    /// <summary>
    /// Off only when asked for, or by default in Development. Anywhere else an unset Mode is Permissive, which needs a
    /// certificate: a missing one fails startup instead of quietly serving secrets in the clear (review: Carl #2).
    /// </summary>
    public static InternalTlsMode Resolve(InternalTlsOptions o, IHostEnvironment env) =>
        o.Mode ?? (env.IsDevelopment() ? InternalTlsMode.Off : InternalTlsMode.Permissive);

    /// <summary>
    /// Resolves the mode once, registers it, and adds the internal TLS listener to Kestrel (Permissive/Enforced); the
    /// operator listener keeps serving /v1 and /health.
    /// </summary>
    public static void Configure(WebApplicationBuilder builder)
    {
        var o = builder.Configuration.GetSection("CredentialAuthority:InternalTls").Get<InternalTlsOptions>() ?? new InternalTlsOptions();
        var mode = Resolve(o, builder.Environment);
        builder.Services.AddSingleton(new InternalTlsBinding(mode, o.Port));
        if (mode == InternalTlsMode.Off)
        {
            return;
        }

        if (string.IsNullOrEmpty(o.CertificatePath))
        {
            throw new InvalidOperationException(
                $"CredentialAuthority:InternalTls:Mode is {mode}{(o.Mode is null ? " (the default outside Development)" : "")} but no CertificatePath is configured.");
        }

        _ = Pins(builder.Configuration); // a malformed pin fails startup, not every handshake
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
                        TlsPin.Matches(cert, Pins(builder.Configuration), DateTimeOffset.UtcNow, TlsPin.ClientAuth, o.ClockSkew);
                });
            });
        });
    }

    /// <summary>The allowlist as configured right now, canonical (configuration reloads, so no restart is needed).</summary>
    private static string[] Pins(IConfiguration configuration) =>
        (configuration.GetSection("CredentialAuthority:InternalTls:ClientFingerprints").Get<string[]>() ?? []).Select(TlsPin.Normalize).ToArray();

    /// <summary>Logs the mode once the app is built.</summary>
    public void Attach()
    {
        if (Mode == InternalTlsMode.Off)
        {
            logger.LogWarning("Internal API mTLS is OFF: /internal/* and Watch are protected only by the service token. Use only in development (#245).");
        }
        else if (options.CurrentValue.InternalTls.ClientFingerprints.Length == 0)
        {
            logger.LogWarning("Internal API mTLS is {Mode} with an empty ClientFingerprints allowlist: no gateway certificate will be accepted.", Mode);
        }
    }

    /// <summary>
    /// Whether a request may use the internal API: when TLS is on, it must have arrived on the internal listener and
    /// carry an allowlisted certificate (Enforced), or carry none, counted (Permissive). Null when allowed, else why not.
    /// </summary>
    public string? Refuse(HttpContext http)
    {
        if (binding.Mode == InternalTlsMode.Off)
        {
            return null;
        }

        if (http.Connection.LocalPort != binding.Port)
        {
            return "the internal API is served only on the internal TLS listener";
        }

        var certificate = http.Connection.ClientCertificate;
        if (certificate is null)
        {
            if (binding.Mode == InternalTlsMode.Enforced)
            {
                return "a client certificate is required";
            }

            Interlocked.Increment(ref _withoutCertCount);
            // The route template, not the raw path: bounded tag cardinality (review: Carl #7).
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "grpc";
            _withoutCert.Add(1, new KeyValuePair<string, object?>("endpoint", route));
            return null;
        }

        var tls = options.CurrentValue.InternalTls;
        var pins = tls.ClientFingerprints.Select(TlsPin.Normalize).ToArray(); // materialised: no lazy work in the check (review: Carl #5)
        return TlsPin.Matches(certificate, pins, DateTimeOffset.UtcNow, TlsPin.ClientAuth, tls.ClockSkew) ? null : "client certificate not allowlisted";
    }
}
