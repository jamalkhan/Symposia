using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

internal sealed class Program
{
    private static async Task<int> Main()
    {
        var tests = new Func<Task>[]
        {
            RunSecureSubmissionFlowAsync,
            RunCommandOrderingFailuresAsync,
            RunAuthFailureAsync
        };

        foreach (var test in tests)
        {
            var testName = test.Method.Name;
            Console.WriteLine($"Running {testName}...");

            try
            {
                await test();
                Console.WriteLine($"PASS {testName}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {testName}: {ex.Message}");
                return 1;
            }
        }

        Console.WriteLine("All integration tests passed.");
        return 0;
    }

    private static async Task RunSecureSubmissionFlowAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var certPath = Path.Combine(tempRoot, "server.pfx");
        var mailRoot = Path.Combine(tempRoot, "maildrop");
        Directory.CreateDirectory(mailRoot);
        CreateDevelopmentCertificate(certPath, "symposia-dev-pass");

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_TLS_CERT_PATH"] = certPath,
            ["SYMPOSIA_SMTP_TLS_CERT_PASSWORD"] = "symposia-dev-pass",
            ["SYMPOSIA_SMTP_AUTH_USERNAME"] = "devuser",
            ["SYMPOSIA_SMTP_AUTH_PASSWORD"] = "devpass",
            ["SYMPOSIA_SMTP_MAIL_ROOT"] = mailRoot
        });

        using var client = await SmtpTestClient.ConnectAsync(port);

        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");

        var ehlo = await client.SendCommandAsync("EHLO localhost");
        ExpectContains(ehlo, "250-STARTTLS");
        ExpectContains(ehlo, "250-AUTH PLAIN LOGIN");

        ExpectSingle(await client.SendCommandAsync("STARTTLS"), "220 2.0.0 Ready to start TLS");
        await client.UpgradeToTlsAsync("localhost");

        var postTlsEhlo = await client.SendCommandAsync("EHLO localhost");
        ExpectNotContains(postTlsEhlo, "250-STARTTLS");
        ExpectContains(postTlsEhlo, "250-AUTH PLAIN LOGIN");

        ExpectSingle(await client.SendCommandAsync("AUTH LOGIN"), "334 VXNlcm5hbWU6");
        ExpectSingle(await client.SendRawLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("devuser"))), "334 UGFzc3dvcmQ6");
        ExpectSingle(await client.SendRawLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("devpass"))), "235 2.7.0 Authentication successful");

        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("DATA"), "354 End data with <CR><LF>.<CR><LF>");
        ExpectSingle(await client.SendDataAsync(new[]
        {
            "Subject: Integration Test",
            "",
            "Hello from the secure integration test."
        }), "250 2.0.0 Ok: queued");
        ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");

        var messageFiles = Directory.GetFiles(mailRoot, "*.eml", SearchOption.AllDirectories);
        if (messageFiles.Length != 1)
        {
            throw new InvalidOperationException($"Expected one persisted message, found {messageFiles.Length}.");
        }

        var persisted = await File.ReadAllTextAsync(messageFiles[0]);
        if (!persisted.Contains("Subject: Integration Test", StringComparison.Ordinal) ||
            !persisted.Contains("Hello from the secure integration test.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted message did not contain the expected content.");
        }
    }

    private static async Task RunCommandOrderingFailuresAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_MAIL_ROOT"] = Path.Combine(tempRoot, "maildrop")
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "503 5.5.1 Send EHLO/HELO first");
        ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "503 5.5.1 Need MAIL FROM before RCPT TO");
        ExpectSingle(await client.SendCommandAsync("DATA"), "503 5.5.1 Need MAIL FROM before DATA");
        ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
    }

    private static async Task RunAuthFailureAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var certPath = Path.Combine(tempRoot, "server.pfx");
        CreateDevelopmentCertificate(certPath, "symposia-dev-pass");

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_TLS_CERT_PATH"] = certPath,
            ["SYMPOSIA_SMTP_TLS_CERT_PASSWORD"] = "symposia-dev-pass",
            ["SYMPOSIA_SMTP_AUTH_USERNAME"] = "devuser",
            ["SYMPOSIA_SMTP_AUTH_PASSWORD"] = "devpass"
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        await client.SendCommandAsync("EHLO localhost");
        ExpectSingle(await client.SendCommandAsync("STARTTLS"), "220 2.0.0 Ready to start TLS");
        await client.UpgradeToTlsAsync("localhost");
        await client.SendCommandAsync("EHLO localhost");
        ExpectSingle(await client.SendCommandAsync("AUTH LOGIN"), "334 VXNlcm5hbWU6");
        ExpectSingle(await client.SendRawLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("devuser"))), "334 UGFzc3dvcmQ6");
        ExpectSingle(await client.SendRawLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("wrong-pass"))), "535 5.7.8 Authentication credentials invalid");
        ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "symposia-smtp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void CreateDevelopmentCertificate(string pfxPath, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var pfxBytes = certificate.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(pfxPath, pfxBytes);
    }

    private static void ExpectSingle(IReadOnlyList<string> actual, string expected)
    {
        if (actual.Count != 1 || !string.Equals(actual[0], expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{string.Join(" | ", actual)}'.");
        }
    }

    private static void ExpectContains(IReadOnlyList<string> actual, string expected)
    {
        if (!actual.Contains(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Expected response to contain '{expected}', got '{string.Join(" | ", actual)}'.");
        }
    }

    private static void ExpectNotContains(IReadOnlyList<string> actual, string unexpected)
    {
        if (actual.Contains(unexpected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Did not expect response to contain '{unexpected}', got '{string.Join(" | ", actual)}'.");
        }
    }
}

internal sealed class SmtpTestClient : IDisposable
{
    private readonly TcpClient _client;
    private Stream _stream;
    private StreamReader _reader;
    private StreamWriter _writer;

    private SmtpTestClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _reader = CreateReader(_stream);
        _writer = CreateWriter(_stream);
    }

    public static async Task<SmtpTestClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return new SmtpTestClient(client);
    }

    public async Task<IReadOnlyList<string>> ReadResponseAsync()
    {
        var lines = new List<string>();

        while (true)
        {
            var line = await _reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            lines.Add(line);
            if (line.Length < 4 || line[3] != '-')
            {
                break;
            }
        }

        return lines;
    }

    public async Task<IReadOnlyList<string>> SendCommandAsync(string command)
    {
        await _writer.WriteLineAsync(command);
        await _writer.FlushAsync();
        return await ReadResponseAsync();
    }

    public async Task<IReadOnlyList<string>> SendRawLineAsync(string line)
    {
        await _writer.WriteLineAsync(line);
        await _writer.FlushAsync();
        return await ReadResponseAsync();
    }

    public async Task<IReadOnlyList<string>> SendDataAsync(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            await _writer.WriteLineAsync(line);
        }

        await _writer.WriteLineAsync(".");
        await _writer.FlushAsync();
        return await ReadResponseAsync();
    }

    public async Task UpgradeToTlsAsync(string serverName)
    {
        var sslStream = new SslStream(_stream, false, static (_, _, _, _) => true);
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = serverName,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        });

        _stream = sslStream;
        _reader = CreateReader(_stream);
        _writer = CreateWriter(_stream);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _reader.Dispose();
        _stream.Dispose();
        _client.Dispose();
    }

    private static StreamReader CreateReader(Stream stream) => new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
    private static StreamWriter CreateWriter(Stream stream) => new(stream, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
}

internal sealed class RunningServer : IAsyncDisposable
{
    private readonly Process _process;

    private RunningServer(Process process)
    {
        _process = process;
    }

    public static async Task<RunningServer> StartAsync(int port, IReadOnlyDictionary<string, string?> extraEnvironment)
    {
        var projectRoot = GetProjectRoot();
        var serverDll = Path.Combine(projectRoot, "EmailProvider", "SymposiaServer", "bin", "Debug", "net9.0", "SymposiaServer.dll");

        var startInfo = new ProcessStartInfo("dotnet", $"\"{serverDll}\"")
        {
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.Environment["SYMPOSIA_SMTP_PORT"] = port.ToString();

        foreach (var pair in extraEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start SMTP server process.");
        var runningServer = new RunningServer(process);
        await runningServer.WaitForServerAsync(port);
        return runningServer;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            await _process.WaitForExitAsync();
            _process.Dispose();
        }
    }

    private async Task WaitForServerAsync(int port)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < timeout)
        {
            if (_process.HasExited)
            {
                var errorOutput = await _process.StandardError.ReadToEndAsync();
                var standardOutput = await _process.StandardOutput.ReadToEndAsync();
                throw new InvalidOperationException($"SMTP server exited unexpectedly.\nSTDOUT:\n{standardOutput}\nSTDERR:\n{errorOutput}");
            }

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException("SMTP server did not start listening in time.");
    }

    private static string GetProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
