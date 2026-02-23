using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Symposia.EmailProvider.SmtpClient;
public class RawSmtpClient : IDisposable
{
    private TcpClient? _client;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public async Task ConnectAsync(string smtpHost, int port = 25, bool useSsl = false)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(smtpHost, port);

        _stream = _client.GetStream();

        if (useSsl)
        {
            var sslStream = new SslStream(_stream, false);
            await sslStream.AuthenticateAsClientAsync(smtpHost);
            _stream = sslStream;
        }

        _reader = new StreamReader(_stream, Encoding.ASCII);
        _writer = new StreamWriter(_stream, Encoding.ASCII) { AutoFlush = true };

        await ExpectAsync("220");  // greeting from server
    }

    public async Task SendEmailAsync(
        string from,
        string to,
        string subject,
        string body,
        bool html = false)
    {
        await SendCommandAsync("EHLO localhost");           // or HELO
        await ExpectAsync("250");

        await SendCommandAsync($"MAIL FROM:<{from}>");
        await ExpectAsync("250");

        await SendCommandAsync($"RCPT TO:<{to}>");
        await ExpectAsync("250");

        await SendCommandAsync("DATA");
        await ExpectAsync("354");

        // Headers
        await _writer.WriteLineAsync($"From: <{from}>");
        await _writer.WriteLineAsync($"To: <{to}>");
        await _writer.WriteLineAsync($"Subject: {subject}");

        if (html)
        {
            await _writer.WriteLineAsync("Content-Type: text/html; charset=utf-8");
        }
        else
        {
            await _writer.WriteLineAsync("Content-Type: text/plain; charset=utf-8");
        }

        await _writer.WriteLineAsync(); // empty line after headers

        // Body
        using var reader = new StringReader(body);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            // Escape lines starting with .
            if (line.StartsWith("."))
                line = "." + line;

            await _writer.WriteLineAsync(line);
        }

        await _writer.WriteLineAsync(".");  // end of data
        await _writer.FlushAsync();

        await ExpectAsync("250");
    }

    private async Task SendCommandAsync(string command)
    {
        if (_writer is null) throw new InvalidOperationException("Not connected");

        Console.WriteLine($"> {command}");
        await _writer.WriteLineAsync(command);
        await _writer.FlushAsync();
    }

    private async Task ExpectAsync(string expectedCodePrefix)
    {
        if (_reader is null) throw new InvalidOperationException("Not connected");

        string response = await _reader.ReadLineAsync() ?? throw new Exception("Connection closed");

        Console.WriteLine($"< {response}");

        if (!response.StartsWith(expectedCodePrefix))
        {
            throw new Exception($"SMTP error - expected {expectedCodePrefix}xx, got: {response}");
        }

        // Multi-line response support (lines starting with code + "-")
        while (response.Length > 3 && response[3] == '-')
        {
            response = await _reader.ReadLineAsync() ?? throw new Exception("Connection closed");
            Console.WriteLine($"< {response}");
        }
    }

    public void Dispose()
    {
        try
        {
            SendCommandAsync("QUIT").GetAwaiter().GetResult();
            ExpectAsync("221").GetAwaiter().GetResult();
        }
        catch { /* ignore on dispose */ }

        _writer?.Dispose();
        _reader?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
    }
}