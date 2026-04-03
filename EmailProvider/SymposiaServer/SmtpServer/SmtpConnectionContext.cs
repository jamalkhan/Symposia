using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;

namespace NativeSmtpReceiver;

public sealed class SmtpConnectionContext : IDisposable
{
    private readonly TcpClient _client;
    private Stream _stream;
    private readonly SmtpServerOptions _options;

    public SmtpConnectionContext(TcpClient client, SmtpServerOptions options)
    {
        _client = client;
        _options = options;
        _stream = client.GetStream();
        Reader = CreateReader(_stream);
        Writer = CreateWriter(_stream);
        ServerName = options.ServerName;
        TlsCertificatePath = options.TlsCertificatePath;
        TlsCertificatePassword = options.TlsCertificatePassword;
    }

    public StreamReader Reader { get; private set; }
    public StreamWriter Writer { get; private set; }
    public bool IsTlsActive => _stream is SslStream;
    public string ServerName { get; }
    public string? TlsCertificatePath { get; }
    public string? TlsCertificatePassword { get; }
    public bool CanStartTls => !string.IsNullOrWhiteSpace(TlsCertificatePath) && File.Exists(TlsCertificatePath);

    public async Task<string?> ReadLineAsync()
    {
        return await Reader.ReadLineAsync();
    }

    public async Task WriteLineAsync(string response)
    {
        await Writer.WriteLineAsync(response);
        await Writer.FlushAsync();
    }

    public async Task UpgradeToTlsAsync()
    {
        if (!CanStartTls)
        {
            throw new InvalidOperationException("TLS certificate not configured.");
        }

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            TlsCertificatePath!,
            TlsCertificatePassword,
            X509KeyStorageFlags.DefaultKeySet);
        var sslStream = new SslStream(_stream, leaveInnerStreamOpen: false);

        await sslStream.AuthenticateAsServerAsync(
            certificate,
            clientCertificateRequired: false,
            enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
            checkCertificateRevocation: false);

        _stream = sslStream;
        Reader = CreateReader(_stream);
        Writer = CreateWriter(_stream);
    }

    public void Dispose()
    {
        Writer.Dispose();
        Reader.Dispose();
        _stream.Dispose();
        _client.Dispose();
    }

    private static StreamReader CreateReader(Stream stream)
    {
        return new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
    }

    private static StreamWriter CreateWriter(Stream stream)
    {
        return new StreamWriter(stream, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };
    }
}
