using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NativeSmtpReceiver;
public class DataLineCommand : ISmtpCommand   // special – handles lines while in DATA mode
{
    public string[] SupportedVerbs => Array.Empty<string>(); // not verb-based


/*
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
*/
    public async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer)
    {
        if (fullLine == ".")
        {
            session.InDataMode = false;

            await PersistEmailAsync(session.MailFrom ?? "unknown", session.Recipients, session.DataLines);

            // For now just print – later: save/queue
            Console.WriteLine("Message received:");
            Console.WriteLine("From: " + session.MailFrom);
            Console.WriteLine("To:   " + string.Join(", ", session.Recipients));
            Console.WriteLine("Body preview:");
            foreach (var l in session.DataLines.Take(10)) 
            {
                Console.WriteLine(l);
            }
            Console.WriteLine("...");

            session.DataLines.Clear();
            await writer.WriteLineAsync("250 2.0.0 Ok: queued");
            await writer.FlushAsync();
        }

        string content = fullLine;
        if (content.StartsWith("..")) content = content[1..];
        session.DataLines.Add(content);

        //return ""; // no response during data body
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