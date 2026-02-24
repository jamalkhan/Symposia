using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NativeSmtpReceiver
{
    // ────────────────────────────────────────────────
    // Main program with command dispatching
    // ────────────────────────────────────────────────
    class Program
    {
        private const int Port = 2525;
        private static readonly IPEndPoint ListenEndpoint = new(IPAddress.Any, Port);

        private static readonly Dictionary<string, ISmtpCommand> CommandMap = BuildCommandMap();

        private static Dictionary<string, ISmtpCommand> BuildCommandMap()
        {
            var map = new Dictionary<string, ISmtpCommand>(StringComparer.OrdinalIgnoreCase);
            var commands = new ISmtpCommand[]
            {
                new EhloCommand(),
                new MailFromCommand(),
                new RcptToCommand(),
                new DataCommand(),
                new QuitCommand(),
                new RsetCommand(),
                new NoopCommand(),
                // Add more later: AuthLoginCommand, StartTlsCommand, VrfyCommand, etc.
            };

            foreach (var cmd in commands)
            {
                foreach (var verb in cmd.SupportedVerbs)
                {
                    map[verb] = cmd;
                }
            }

            return map;
        }

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
                    _ = HandleClientAsync(client);
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
                // IMPORTANT: Disable Nagle's algorithm for low-latency protocol responses
                client.NoDelay = true;  // ← This helps A LOT with pipelining

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) 
                { 
                    AutoFlush = true,
                    NewLine = "\r\n" 
                };

                // Greeting + immediate flush
                await writer.WriteLineAsync("220 native-smtp.local ESMTP Ready");
                await writer.FlushAsync();

                var session = new SmtpSession();
                var dataLineHandler = new DataLineCommand();

                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) break;

                    Console.WriteLine($"> {line}");

                    string trimmed = line.Trim();
                    string upper = trimmed.ToUpperInvariant();

                    if (session.InDataMode)
                    {
                        await dataLineHandler.ExecuteAsync(line, null, session, writer);
                    }
                    else
                    {
                        string verb = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                        string? arg = trimmed.Length > verb.Length ? trimmed[(verb.Length + 1)..].Trim() : null;

                        if (CommandMap.TryGetValue(verb, out var command))
                        {
                            await command.ExecuteAsync(trimmed, arg, session, writer);
                        }
                        else
                        {
                            await new UnknownCommand().ExecuteAsync(trimmed, arg, session, writer);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* QUIT */ }
            catch (Exception ex)
            {
                Console.WriteLine($"Client error: {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        }
    }
}



/*using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SymposiaServer
{
    class Program
    {
        private const int Port = 2525;                  // Use 2525 for local testing (avoid needing root)
        private static readonly IPEndPoint ListenEndpoint = new(IPAddress.Any, Port);
        private static bool _useTls = false;            // Set to true after implementing cert

        // Hard-coded list of supported domains
        private static readonly HashSet<string> SupportedDomains = new(StringComparer.OrdinalIgnoreCase)
        {
            "symposia.com"
            // Add more domains as needed
        };

        // Hard-coded list of valid recipient addresses
        private static readonly HashSet<string> ValidRecipients = new(StringComparer.OrdinalIgnoreCase)
        {
            "jamal@symposia.com",
            "admin@symposia.com"
        };

        static async Task WriteResponse(Stream? stream, string response)
        {
            var bytes = Encoding.ASCII.GetBytes(response + "\r\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

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
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                // Optional: STARTTLS support (requires a server certificate)
                // For now we do plain text. See notes below for TLS.


                await WriteResponse(stream, "220 THIS IS THE NEW BUILD ESMTP Ready");

                string? from = null;
                var recipients = new List<string>();
                var messageLines = new List<string>();
                bool inData = false;

                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) break;

                    Console.WriteLine($"> {line}");

                    // Normalize: remove leading/trailing whitespace, convert to upper
                    string command = line.Trim().ToUpperInvariant();

                    // ── ADD THESE TWO LINES ──
                    Console.WriteLine($"[PRE-DATA] About to check for DATA, current upper='{command}'");
                    if (inData)
                    {
                        Console.WriteLine($"[IN-DATA] COMMAND='{command}'");
                        if (line == ".")
                        {
                            Console.WriteLine($"[IN-DATA Writing Message] COMMAND='{command}'");
                            inData = false;
                            await WriteResponse(stream, "250 2.0.0 Ok: queued");

                            Console.WriteLine("Message received: xyz");
                            await PersistEmailAsync(from ?? "unknown", recipients, messageLines);

                            // debug log
                            Console.WriteLine("Message received:");
                            Console.WriteLine("From: " + from);
                            Console.WriteLine("To:   " + string.Join(", ", recipients));
                            Console.WriteLine("Body:");
                            foreach (var msgLine in messageLines) Console.WriteLine(msgLine);
                            Console.WriteLine("--- end of message ---");
                        }
                        else
                        {
                            // Handle dot-stuffing
                            if (line.StartsWith(".."))
                                line = line.Substring(1);

                            messageLines.Add(line);
                        }
                    }
                    else if (command.StartsWith("EHLO") || command.StartsWith("HELO"))
                    {
                        await WriteResponse(stream, "250-native-smtp.local Hello");
                        await WriteResponse(stream, "250-8BITMIME");
                        await WriteResponse(stream, "250-SIZE 10485760");

                        if (!_useTls)
                        {
                            await WriteResponse(stream, "250 STARTTLS");
                        }
                        else
                        {
                            await WriteResponse(stream, "250 OK");
                        }
                    }
                    else if (command.StartsWith("MAIL FROM:"))
                    {
                        from = ParseAddress(line.Substring(10).Trim());
                        await WriteResponse(stream, "250 2.1.0 Ok");
                    }
                    else if (command.StartsWith("RCPT TO:"))
                    {
                        var rcpt = ParseAddress(line.Substring(8).Trim());
                        var parts = rcpt.Split('@');
                        if (parts.Length != 2)
                        {
                            await WriteResponse(stream, "501 5.1.3 Bad recipient address syntax");
                            continue;
                        }

                        var domain = parts[1];

                        if (!SupportedDomains.Contains(domain))
                        {
                            await WriteResponse(stream, "550 5.1.2 Domain not hosted here");
                            continue;
                        }

                        if (!ValidRecipients.Contains(rcpt))
                        {
                            await WriteResponse(stream, "550 5.1.1 User unknown"); 
                            continue;
                        }

                        recipients.Add(rcpt);
                        await WriteResponse(stream, "250 2.1.5 Ok");
                    }
                    else if (command == "DATA")
                    {
                        Console.WriteLine("[FORCE] ENTERED DATA BLOCK !!!!!!!!!");

                        if (recipients.Count == 0)
                        {
                            await WriteResponse(stream, "503 5.5.1 No valid recipients");
                            continue;
                        }

                        inData = true;

                        await WriteResponse(stream, "354 Start mail input; end with <CRLF>.<CRLF>");
                    }
                    else if (command == "QUIT")
                    {
                        await WriteResponse(stream, "221 2.0.0 Bye");
                        break;
                    }
                    else if (command == "RSET")
                    {
                        from = null;
                        recipients.Clear();
                        messageLines.Clear();
                        inData = false;
                        await WriteResponse(stream, "250 2.0.0 Ok");
                    }
                    else if (command == "NOOP")
                    {
                        await WriteResponse(stream, "250 2.0.0 Ok");
                    }
                    else if (command.StartsWith("STARTTLS") && !_useTls)
                    {
                        await WriteResponse(stream, "502 STARTTLS not implemented yet");
                    }
                    else
                    {
                        await WriteResponse(stream, "500 5.5.2 Unrecognized command");
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

        private static async Task PersistEmailAsync(string from, List<string> recipients, List<string> messageLines)
        {
            Console.WriteLine("Persisting email to disk...");
            var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "emails"); // Base folder for all emails

            foreach (var recipient in recipients)
            {
                var parts = recipient.Split('@');
                var domain = parts[1]; // Assuming already validated
                var recipientDir = Path.Combine(baseDir, domain, recipient); // <domain>\<recipient address>

                Directory.CreateDirectory(recipientDir); // Creates if not exists

                var now = DateTime.UtcNow; // Use UTC to avoid timezone issues
                var datePart = now.ToString("yyyyMMdd_HHmmss");

                var fromCleaned = SanitizeFileNamePart(from);
                var toCleaned = SanitizeFileNamePart(recipient);

                var fileName = $"{datePart}_{fromCleaned}_{toCleaned}.eml";
                var filePath = Path.Combine(recipientDir, fileName);

                // Write the .eml file (raw message lines)
                Console.WriteLine($"Persisting email to disk at {filePath}...");
                using var fileWriter = new StreamWriter(filePath, false, Encoding.UTF8);

                foreach (var line in messageLines)
                {
                    await fileWriter.WriteLineAsync(line);
                }

                Console.WriteLine($"Email persisted to: {filePath}");
            }
        }

        private static string ParseAddress(string s)
        {
            s = s.Trim();
            if (s.StartsWith("<") && s.EndsWith(">")) s = s[1..^1];
            return s;
        }

        private static string SanitizeFileNamePart(string input)
        {
            if (string.IsNullOrEmpty(input)) return "unknown";

            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                sb.Append(invalidChars.Contains(c) ? '_' : c);
            }
            return sb.ToString();
        }
    }
}*/