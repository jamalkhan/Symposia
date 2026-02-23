using Symposia.EmailProvider.SmtpClient;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine("SMTP Raw Client Test");
        Console.WriteLine("=====================");


        try
        {
            using var smtp = new RawSmtpClient();
            Console.WriteLine("Connecting to smtp.gmail.com:587 (STARTTLS expected)...");
            await smtp.ConnectAsync("smtp.gmail.com", 587, useSsl: false);
            // Initial server greeting should already be read in ConnectAsync
            // Now do the basic handshake
            await smtp.SendEmailAsync(
                from: "test@emai.lk",
                to: "jamal.h.khan@gmail.com",
                subject: "Test Email",
                body:  "This is a test email sent using a raw SMTP client implemented in C#.",
                html: false
            );

            Console.WriteLine("Basic EHLO successful");

            // ────────────────────────────────────────────────
            //   NEXT STEPS YOU STILL NEED TO IMPLEMENT:
            // ────────────────────────────────────────────────
            // 1. Call StartTlsAsync()  ← add this method to RawSmtpClient
            // 2. Re-issue EHLO after TLS
            // 3. Authenticate (AUTH LOGIN + base64 username/password or app password)
            // 4. Then SendEmailAsync will work

            // For now this stops here so you can see it connect & handshake

            // Uncomment the following lines once STARTTLS + AUTH are implemented:
            /*
            Console.WriteLine("Upgrading to TLS...");
            await smtp.StartTlsAsync();

            Console.WriteLine("Re-sending EHLO after TLS upgrade");
            await smtp.SendCommandAsync("EHLO test.localhost");
            await smtp.ExpectAsync("250");

            // Replace with real values – use App Password for Gmail!
            // await smtp.AuthenticateAsync("your@gmail.com", "abcd-efgh-ijkl-mnop");

            Console.WriteLine("Sending test email...");
            await smtp.SendEmailAsync(
                from:    "your@gmail.com",
                to:      "recipient@example.com",
                subject: "Raw SMTP Test from .NET",
                body:    "<h1>Hello from raw SMTP!</h1><p>This message was sent using a hand-crafted SMTP client.</p>",
                html:    true
            );

            Console.WriteLine("Email appears to have been accepted by the server ✓");
            */

        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error occurred:");
            Console.WriteLine(ex.Message);

            if (ex.InnerException != null)
            {
                Console.WriteLine("Inner exception:");
                Console.WriteLine(ex.InnerException.Message);
            }

            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(true);
    }
}