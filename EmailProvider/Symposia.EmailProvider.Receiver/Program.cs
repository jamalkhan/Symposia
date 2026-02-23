using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NativeSmtpReceiver
{
    class Program
    {
        private const int Port = 2525;                  // Use 2525 for local testing (avoid needing root)
        private static readonly IPEndPoint ListenEndpoint = new(IPAddress.Any, Port);
        private static bool _useTls = false;            // Set to true after implementing cert

        static async Task Main(string[] args)
        {
            Console.WriteLine($"Starting minimal SMTP receiver on port {Port} ...");

            var listener = new TcpListener(ListenEndpoint);
            listener.Start();

            while (true)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);  // fire-and-forget per connection
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Accept error: {ex.Message}");
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                await using var stream = client.GetStream();
                await using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

                // Optional: STARTTLS support (requires a server certificate)
                // For now we do plain text. See notes below for TLS.

                await writer.WriteLineAsync("220 native-smtp.local ESMTP Ready");

                string? from = null;
                var recipients = new List<string>();
                var messageLines = new List<string>();
                bool inData = false;

                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) break;

                    Console.WriteLine($"> {line}");

                    string upper = line.ToUpperInvariant();

                    if (inData)
                    {
                        if (line == ".")
                        {
                            inData = false;
                            await writer.WriteLineAsync("250 2.0.0 Ok: queued");
                            Console.WriteLine("Message received:");
                            Console.WriteLine("From: " + from);
                            Console.WriteLine("To:   " + string.Join(", ", recipients));
                            Console.WriteLine("Body:");
                            foreach (var msgLine in messageLines) Console.WriteLine(msgLine);
                            Console.WriteLine("--- end of message ---");
                        }
                        else
                        {
                            // Un-escape dot-stuffing
                            if (line.StartsWith("..")) line = line.Substring(1);
                            messageLines.Add(line);
                        }
                    }
                    else if (upper.StartsWith("EHLO") || upper.StartsWith("HELO"))
                    {
                        await writer.WriteLineAsync("250-native-smtp.local Hello");
                        await writer.WriteLineAsync("250-8BITMIME");
                        await writer.WriteLineAsync("250-SIZE 10485760");
                        if (!_useTls) await writer.WriteLineAsync("250 STARTTLS"); // advertise only if we can do it
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (upper.StartsWith("MAIL FROM:"))
                    {
                        from = ParseAddress(line.Substring(10).Trim());
                        await writer.WriteLineAsync("250 2.1.0 Ok");
                    }
                    else if (upper.StartsWith("RCPT TO:"))
                    {
                        var rcpt = ParseAddress(line.Substring(8).Trim());
                        recipients.Add(rcpt);
                        await writer.WriteLineAsync("250 2.1.5 Ok");
                    }
                    else if (upper == "DATA")
                    {
                        inData = true;
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    }
                    else if (upper == "QUIT")
                    {
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        break;
                    }
                    else if (upper == "RSET")
                    {
                        from = null;
                        recipients.Clear();
                        messageLines.Clear();
                        inData = false;
                        await writer.WriteLineAsync("250 2.0.0 Ok");
                    }
                    else if (upper == "NOOP")
                    {
                        await writer.WriteLineAsync("250 2.0.0 Ok");
                    }
                    else if (upper.StartsWith("STARTTLS") && !_useTls)
                    {
                        await writer.WriteLineAsync("502 STARTTLS not implemented yet");
                    }
                    else
                    {
                        await writer.WriteLineAsync("502 Command not implemented");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client error: {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        }

        private static string ParseAddress(string s)
        {
            s = s.Trim();
            if (s.StartsWith("<") && s.EndsWith(">")) s = s[1..^1];
            return s;
        }
    }
}