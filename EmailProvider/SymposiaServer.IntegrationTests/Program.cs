using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSmtpReceiver;

internal sealed class Program
{
    private const string FileSystemStorageType = "fileSystem";

    private static async Task<int> Main()
    {
        var tests = new Func<Task>[]
        {
            RunSecureSubmissionFlowAsync,
            RunMailboxStorageRoutingAsync,
            RunMailboxReadModelAsync,
            RunDashboardHttpApiAsync,
            RunMixedRecipientHandlingAsync,
            RunCommandOrderingFailuresAsync,
            RunProtocolNegativeCasesAsync,
            RunAuthUnavailableAsync,
            RunStorageFailureAsync,
            RunUnsupportedProviderFailureAsync,
            RunConfigFailureCasesAsync,
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
        var recipientConfigPath = Path.Combine(tempRoot, "mailboxes.json");
        Directory.CreateDirectory(mailRoot);
        CreateDevelopmentCertificate(certPath, "symposia-dev-pass");
        await WriteHostingConfigAsync(
            recipientConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, mailRoot)],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal"), new TestMailbox("admin@symposia.com", "admin")])]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_TLS_CERT_PATH"] = certPath,
            ["SYMPOSIA_SMTP_TLS_CERT_PASSWORD"] = "symposia-dev-pass",
            ["SYMPOSIA_SMTP_AUTH_USERNAME"] = "devuser",
            ["SYMPOSIA_SMTP_AUTH_PASSWORD"] = "devpass",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = recipientConfigPath
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

        var messageFiles = Directory.GetFiles(Path.Combine(mailRoot, "mailboxes", "jamal", "messages"), "*.eml", SearchOption.TopDirectoryOnly);
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

        ExpectAddressPointer(mailRoot, "symposia.com", "jamal@symposia.com", "jamal");
    }

    private static async Task RunMailboxStorageRoutingAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var primaryRoot = Path.Combine(tempRoot, "primary-maildrop");
        var overrideRoot = Path.Combine(tempRoot, "override-maildrop");
        var recipientConfigPath = Path.Combine(tempRoot, "mailboxes.json");

        await WriteHostingConfigAsync(
            recipientConfigPath,
            [
                new TestStorageProvider("local-primary", FileSystemStorageType, primaryRoot),
                new TestStorageProvider("local-override", FileSystemStorageType, overrideRoot)
            ],
            [
                new TestDomain("domain1.com", "local-primary",
                [
                    new TestMailbox("jamal@domain1.com", "jamal"),
                    new TestMailbox("admin@domain1.com", "admin", "local-override")
                ]),
                new TestDomain("domain2.com", "local-primary",
                [
                    new TestMailbox("jamal@domain2.com", "jamal")
                ])
            ]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = recipientConfigPath
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@domain1.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<admin@domain1.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@domain2.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("DATA"), "354 End data with <CR><LF>.<CR><LF>");
        ExpectSingle(await client.SendDataAsync(["Subject: Routing Test", "", "Mailbox routing works."]), "250 2.0.0 Ok: queued");
        ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");

        ExpectMailboxMessagePersisted(primaryRoot, "jamal", 1);
        ExpectMailboxMessagePersisted(overrideRoot, "admin", 1);
        ExpectAddressPointer(primaryRoot, "domain1.com", "jamal@domain1.com", "jamal");
        ExpectAddressPointer(primaryRoot, "domain2.com", "jamal@domain2.com", "jamal");
        ExpectAddressPointer(overrideRoot, "domain1.com", "admin@domain1.com", "admin");
    }

    private static async Task RunMixedRecipientHandlingAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var mailRoot = Path.Combine(tempRoot, "maildrop");
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, mailRoot)],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<missing@symposia.com>"), "550 5.1.1 Mailbox unavailable");
        ExpectSingle(await client.SendCommandAsync("DATA"), "354 End data with <CR><LF>.<CR><LF>");
        ExpectSingle(await client.SendDataAsync(["Subject: Mixed", "", "One valid recipient only."]), "250 2.0.0 Ok: queued");
        ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");

        ExpectMailboxMessagePersisted(mailRoot, "jamal", 1);
        ExpectAddressPointer(mailRoot, "symposia.com", "jamal@symposia.com", "jamal");
        ExpectNoAddressPointer(mailRoot, "symposia.com", "missing@symposia.com");
    }

    private static async Task RunMailboxReadModelAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var mailRoot = Path.Combine(tempRoot, "maildrop");
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, mailRoot)],
            [
                new TestDomain("domain1.com", "local-default", [new TestMailbox("jamal@domain1.com", "jamal")]),
                new TestDomain("domain2.com", "local-default", [new TestMailbox("jamal@domain2.com", "jamal")])
            ]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath
        });

        using (var client = await SmtpTestClient.ConnectAsync(port))
        {
            ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
            ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
            ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
            ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@domain1.com>"), "250 2.1.5 Ok");
            ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@domain2.com>"), "250 2.1.5 Ok");
            ExpectSingle(await client.SendCommandAsync("DATA"), "354 End data with <CR><LF>.<CR><LF>");
            ExpectSingle(await client.SendDataAsync(["Subject: Mailbox Read", "", "Logical mailbox storage."]), "250 2.0.0 Ok: queued");
            ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
        }

        var directory = HostingDirectory.Load(hostingConfigPath);
        var readService = new MailboxReadService(directory, NullLoggerFactory.Instance, NullLogger<MailboxReadService>.Instance);

        var bindings = readService.GetMailboxBindings("jamal");
        if (bindings.Count != 2)
        {
            throw new InvalidOperationException($"Expected mailbox 'jamal' to have 2 bindings, found {bindings.Count}.");
        }

        var summaries = await readService.ListMessagesAsync("jamal");
        if (summaries.Count != 1)
        {
            throw new InvalidOperationException($"Expected mailbox 'jamal' to have 1 logical message, found {summaries.Count}.");
        }

        var summary = summaries[0];
        if (!string.Equals(summary.Subject, "Mailbox Read", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected subject 'Mailbox Read', got '{summary.Subject}'.");
        }

        if (!summary.DeliveredAddresses.Contains("jamal@domain1.com", StringComparer.OrdinalIgnoreCase) ||
            !summary.DeliveredAddresses.Contains("jamal@domain2.com", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mailbox summary did not include both routed addresses.");
        }

        var storedMessage = await readService.GetMessageAsync("jamal", summary.MessageId)
            ?? throw new InvalidOperationException("Expected stored mailbox message to be readable.");
        if (!storedMessage.RawMessage.Contains("Logical mailbox storage.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stored mailbox message did not contain expected raw content.");
        }

        if (!string.Equals(storedMessage.Metadata.MailboxId, "jamal", StringComparison.Ordinal) ||
            !string.Equals(storedMessage.Metadata.Subject, "Mailbox Read", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stored mailbox message metadata did not match expected mailbox identity.");
        }
    }

    private static async Task RunDashboardHttpApiAsync()
    {
        var smtpPort = GetFreePort();
        var httpPort = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var mailRoot = Path.Combine(tempRoot, "maildrop");
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, mailRoot)],
            [
                new TestDomain("domain1.com", "local-default", [new TestMailbox("jamal@domain1.com", "jamal")]),
                new TestDomain("domain2.com", "local-default", [new TestMailbox("jamal@domain2.com", "jamal"), new TestMailbox("admin@domain2.com", "admin")])
            ]);

        await using var server = await RunningServer.StartAsync(smtpPort, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath,
            ["SYMPOSIA_HTTP_PORT"] = httpPort.ToString()
        });

        using (var client = await SmtpTestClient.ConnectAsync(smtpPort))
        {
            ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
            ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
            ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
            ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@domain1.com>"), "250 2.1.5 Ok");
            ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@domain2.com>"), "250 2.1.5 Ok");
            ExpectSingle(await client.SendCommandAsync("RCPT TO:<admin@domain2.com>"), "250 2.1.5 Ok");
            ExpectSingle(await client.SendCommandAsync("DATA"), "354 End data with <CR><LF>.<CR><LF>");
            ExpectSingle(await client.SendDataAsync(["Subject: Dashboard", "", "Dashboard API coverage."]), "250 2.0.0 Ok: queued");
            ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
        }

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{httpPort}")
        };

        var dashboardHtml = await WaitForHttpStringAsync(httpClient, "/");
        ExpectContains(dashboardHtml, "Mailbox Delivery Dashboard");
        ExpectContains(dashboardHtml, "/api/dashboard/summary");

        var summaryJson = await WaitForHttpStringAsync(httpClient, "/api/dashboard/summary");
        var payload = JsonNode.Parse(summaryJson)?.AsObject()
            ?? throw new InvalidOperationException("Dashboard API response could not be parsed.");
        var domains = payload["domains"]?.AsArray()
            ?? throw new InvalidOperationException("Dashboard API response did not include domains.");
        if (domains.Count != 2)
        {
            throw new InvalidOperationException($"Expected 2 domains in dashboard response, found {domains.Count}.");
        }

        var domain1 = domains.Single(node => string.Equals(node?["domainName"]?.GetValue<string>(), "domain1.com", StringComparison.OrdinalIgnoreCase))?.AsObject()
            ?? throw new InvalidOperationException("Missing domain1.com in dashboard response.");
        var domain2 = domains.Single(node => string.Equals(node?["domainName"]?.GetValue<string>(), "domain2.com", StringComparison.OrdinalIgnoreCase))?.AsObject()
            ?? throw new InvalidOperationException("Missing domain2.com in dashboard response.");

        ExpectEqual(domain1["messageCount"]?.GetValue<int>(), 1, "domain1.com message count");
        ExpectEqual(domain2["messageCount"]?.GetValue<int>(), 2, "domain2.com message count");

        var domain2Mailboxes = domain2["mailboxes"]?.AsArray()
            ?? throw new InvalidOperationException("Expected mailboxes for domain2.com.");
        var jamalDomain2 = domain2Mailboxes.Single(node => string.Equals(node?["address"]?.GetValue<string>(), "jamal@domain2.com", StringComparison.OrdinalIgnoreCase))?.AsObject()
            ?? throw new InvalidOperationException("Missing jamal@domain2.com in dashboard response.");
        var adminDomain2 = domain2Mailboxes.Single(node => string.Equals(node?["address"]?.GetValue<string>(), "admin@domain2.com", StringComparison.OrdinalIgnoreCase))?.AsObject()
            ?? throw new InvalidOperationException("Missing admin@domain2.com in dashboard response.");

        ExpectEqual(jamalDomain2["messageCount"]?.GetValue<int>(), 1, "jamal@domain2.com message count");
        ExpectEqual(adminDomain2["messageCount"]?.GetValue<int>(), 1, "admin@domain2.com message count");
    }

    private static async Task RunCommandOrderingFailuresAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var recipientConfigPath = Path.Combine(tempRoot, "mailboxes.json");
        await WriteHostingConfigAsync(
            recipientConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, Path.Combine(tempRoot, "maildrop"))],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = recipientConfigPath
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "503 5.5.1 Send EHLO/HELO first");
        ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "503 5.5.1 Need MAIL FROM before RCPT TO");
        ExpectSingle(await client.SendCommandAsync("DATA"), "503 5.5.1 Need MAIL FROM before DATA");
        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<unknown@symposia.com>"), "550 5.1.1 Mailbox unavailable");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@example.com>"), "550 5.1.2 Domain not hosted here");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
    }

    private static async Task RunProtocolNegativeCasesAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var certPath = Path.Combine(tempRoot, "server.pfx");
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");
        CreateDevelopmentCertificate(certPath, "symposia-dev-pass");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, Path.Combine(tempRoot, "maildrop"))],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_TLS_CERT_PATH"] = certPath,
            ["SYMPOSIA_SMTP_TLS_CERT_PASSWORD"] = "symposia-dev-pass",
            ["SYMPOSIA_SMTP_AUTH_USERNAME"] = "devuser",
            ["SYMPOSIA_SMTP_AUTH_PASSWORD"] = "devpass",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath
        });

        using (var client = await SmtpTestClient.ConnectAsync(port))
        {
            ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
            ExpectSingle(await client.SendCommandAsync("STARTTLS"), "503 5.5.1 Send EHLO/HELO first");
            ExpectSingle(await client.SendCommandAsync("AUTH LOGIN"), "503 5.5.1 Send EHLO/HELO first");
            ExpectSingle(await client.SendCommandAsync("MAIL TO:<bad@example.com>"), "503 5.5.1 Send EHLO/HELO first");
            ExpectSingle(await client.SendCommandAsync("MAIL FROM:<>"), "503 5.5.1 Send EHLO/HELO first");
            ExpectSingle(await client.SendRawLineAsync(""), "500 5.5.2 Syntax error, command unrecognized");
            ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
        }

        using (var client = await SmtpTestClient.ConnectAsync(port))
        {
            ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
            ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250-STARTTLS");
            ExpectSingle(await client.SendCommandAsync("MAIL TO:<bad@example.com>"), "501 5.5.4 Syntax: MAIL FROM:<address>");
            ExpectSingle(await client.SendCommandAsync("NOOP"), "250 2.0.0 Ok");
            var helpResponse = await client.SendCommandAsync("HELP");
            ExpectContains(helpResponse, "214-Commands supported:");
            ExpectContains(helpResponse, "214 EHLO HELO MAIL RCPT DATA RSET NOOP QUIT STARTTLS AUTH VRFY EXPN HELP");
            ExpectSingle(await client.SendCommandAsync("VRFY"), "501 5.5.4 Syntax: VRFY mailbox");
            ExpectSingle(await client.SendCommandAsync("EXPN"), "501 5.5.4 Syntax: EXPN mailing-list");
            ExpectSingle(await client.SendCommandAsync("BOGUS"), "502 5.5.1 Command not implemented");
            ExpectSingle(await client.SendCommandAsync("MAIL FROM:<>"), "501 5.1.7 Invalid address");
            ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
            ExpectSingle(await client.SendCommandAsync("RCPT BAD"), "501 5.5.4 Syntax: RCPT TO:<address>");
            ExpectSingle(await client.SendCommandAsync("RCPT TO:<>"), "501 5.1.7 Invalid address");
            ExpectSingle(await client.SendCommandAsync("RSET"), "250 2.0.0 Ok");
            ExpectSingle(await client.SendCommandAsync("DATA"), "503 5.5.1 Need MAIL FROM before DATA");
            ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
        }

        using (var client = await SmtpTestClient.ConnectAsync(port))
        {
            ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
            ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250-STARTTLS");
            ExpectSingle(await client.SendCommandAsync("STARTTLS"), "220 2.0.0 Ready to start TLS");
            await client.UpgradeToTlsAsync("localhost");
            ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
            ExpectSingle(await client.SendCommandAsync("STARTTLS"), "503 5.5.1 TLS already active");
            ExpectSingle(await client.SendCommandAsync("AUTH"), "501 5.5.4 Syntax: AUTH mechanism [initial-response]");
            ExpectSingle(await client.SendCommandAsync("AUTH CRAM-MD5"), "504 5.5.4 Unrecognized authentication type");
            ExpectSingle(await client.SendCommandAsync("AUTH PLAIN !!!"), "501 5.5.2 Invalid base64 payload");
            ExpectSingle(await client.SendCommandAsync("AUTH PLAIN"), "334 ");
            ExpectSingle(await client.SendRawLineAsync("*"), "501 5.7.0 Authentication cancelled");
            ExpectSingle(await client.SendCommandAsync("AUTH LOGIN"), "334 VXNlcm5hbWU6");
            ExpectSingle(await client.SendRawLineAsync("*"), "501 5.7.0 Authentication cancelled");
            ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
        }
    }

    private static async Task RunAuthUnavailableAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, Path.Combine(tempRoot, "maildrop"))],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
        ExpectSingle(await client.SendCommandAsync("AUTH LOGIN"), "454 4.7.0 Authentication unavailable");
        ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
    }

    private static async Task RunAuthFailureAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var certPath = Path.Combine(tempRoot, "server.pfx");
        var recipientConfigPath = Path.Combine(tempRoot, "mailboxes.json");
        CreateDevelopmentCertificate(certPath, "symposia-dev-pass");
        await WriteHostingConfigAsync(
            recipientConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, Path.Combine(tempRoot, "maildrop"))],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_TLS_CERT_PATH"] = certPath,
            ["SYMPOSIA_SMTP_TLS_CERT_PASSWORD"] = "symposia-dev-pass",
            ["SYMPOSIA_SMTP_AUTH_USERNAME"] = "devuser",
            ["SYMPOSIA_SMTP_AUTH_PASSWORD"] = "devpass",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = recipientConfigPath
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

    private static async Task RunStorageFailureAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var certPath = Path.Combine(tempRoot, "server.pfx");
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");
        var blockedRoot = Path.Combine(tempRoot, "maildrop-blocker");
        await File.WriteAllTextAsync(blockedRoot, "not a directory");
        CreateDevelopmentCertificate(certPath, "symposia-dev-pass");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, blockedRoot)],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath,
            ["SYMPOSIA_SMTP_TLS_CERT_PATH"] = certPath,
            ["SYMPOSIA_SMTP_TLS_CERT_PASSWORD"] = "symposia-dev-pass"
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250-STARTTLS");
        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("DATA"), "354 End data with <CR><LF>.<CR><LF>");
        ExpectConnectionClosed(await client.SendDataAsync(["Subject: Failure", "", "filesystem should fail"]));
    }

    private static async Task RunUnsupportedProviderFailureAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("aws-mail", "awsS3", Path.Combine(tempRoot, "unused"))],
            [new TestDomain("symposia.com", "aws-mail", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("DATA"), "354 End data with <CR><LF>.<CR><LF>");
        ExpectConnectionClosed(await client.SendDataAsync(["Subject: Unsupported", "", "unsupported provider should fail"]));
    }

    private static async Task RunConfigFailureCasesAsync()
    {
        var missingFilePath = Path.Combine(CreateTempDirectory(), "missing-mailboxes.json");
        var missingFileFailure = await RunningServer.StartExpectFailureAsync(
            GetFreePort(),
            new Dictionary<string, string?>
            {
                ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
                ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = missingFilePath
            });
        ExpectContains(missingFileFailure, "Hosting configuration file not found");

        var tempRoot = CreateTempDirectory();
        var invalidProviderConfigPath = Path.Combine(tempRoot, "invalid-provider.json");
        await WriteHostingConfigAsync(
            invalidProviderConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, Path.Combine(tempRoot, "maildrop"))],
            [new TestDomain("symposia.com", "missing-provider", [new TestMailbox("jamal@symposia.com", "jamal")])]);
        var invalidProviderFailure = await RunningServer.StartExpectFailureAsync(
            GetFreePort(),
            new Dictionary<string, string?>
            {
                ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
                ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = invalidProviderConfigPath
            });
        ExpectContains(invalidProviderFailure, "Storage provider 'missing-provider'");

        var mismatchConfigPath = Path.Combine(tempRoot, "mismatch.json");
        await WriteHostingConfigAsync(
            mismatchConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, Path.Combine(tempRoot, "maildrop-2"))],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@example.com", "jamal")])]);
        var mismatchFailure = await RunningServer.StartExpectFailureAsync(
            GetFreePort(),
            new Dictionary<string, string?>
            {
                ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
                ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = mismatchConfigPath
            });
        ExpectContains(mismatchFailure, "does not belong to configured domain");
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

    private static async Task WriteHostingConfigAsync(
        string path,
        IReadOnlyList<TestStorageProvider> storageProviders,
        IReadOnlyList<TestDomain> domains)
    {
        var config = new
        {
            StorageProviders = storageProviders.Select(static provider => provider.ToJsonShape()).ToArray(),
            Domains = domains.Select(static domain => domain.ToJsonShape()).ToArray()
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
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

    private static void ExpectContains(string actual, string expected)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text to contain '{expected}', got '{actual}'.");
        }
    }

    private static void ExpectMailboxMessagePersisted(string rootPath, string mailboxId, int expectedCount)
    {
        var mailboxPath = Path.Combine(rootPath, "mailboxes", mailboxId, "messages");
        if (!Directory.Exists(mailboxPath))
        {
            throw new InvalidOperationException($"Expected mailbox storage under '{mailboxPath}'.");
        }

        var messageFiles = Directory.GetFiles(mailboxPath, "*.eml", SearchOption.TopDirectoryOnly);
        if (messageFiles.Length != expectedCount)
        {
            throw new InvalidOperationException($"Expected {expectedCount} persisted message(s) for mailbox '{mailboxId}' under '{mailboxPath}', found {messageFiles.Length}.");
        }

        var metadataFiles = Directory.GetFiles(mailboxPath, "*.json", SearchOption.TopDirectoryOnly);
        if (metadataFiles.Length != expectedCount)
        {
            throw new InvalidOperationException($"Expected {expectedCount} metadata file(s) for mailbox '{mailboxId}' under '{mailboxPath}', found {metadataFiles.Length}.");
        }
    }

    private static void ExpectAddressPointer(string rootPath, string domain, string address, string expectedMailboxId)
    {
        var pointerPath = Path.Combine(rootPath, "addresses", domain, address, "pointer.json");
        if (!File.Exists(pointerPath))
        {
            throw new InvalidOperationException($"Expected address pointer at '{pointerPath}'.");
        }

        var json = JsonNode.Parse(File.ReadAllText(pointerPath))?.AsObject()
            ?? throw new InvalidOperationException($"Pointer file at '{pointerPath}' could not be parsed.");
        var actualMailboxId = json["MailboxId"]?.GetValue<string>();
        if (!string.Equals(actualMailboxId, expectedMailboxId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected pointer for '{address}' to target mailbox '{expectedMailboxId}', got '{actualMailboxId}'.");
        }
    }

    private static void ExpectNoAddressPointer(string rootPath, string domain, string address)
    {
        var pointerPath = Path.Combine(rootPath, "addresses", domain, address, "pointer.json");
        if (File.Exists(pointerPath))
        {
            throw new InvalidOperationException($"Did not expect address pointer at '{pointerPath}'.");
        }
    }

    private static void ExpectConnectionClosed(IReadOnlyList<string> actual)
    {
        if (actual.Count != 0)
        {
            throw new InvalidOperationException($"Expected connection close, got '{string.Join(" | ", actual)}'.");
        }
    }

    private static void ExpectEqual<T>(T? actual, T expected, string label)
        where T : struct, IEquatable<T>
    {
        if (!actual.HasValue || !actual.Value.Equals(expected))
        {
            throw new InvalidOperationException($"Expected {label} to be '{expected}', got '{actual?.ToString() ?? "null"}'.");
        }
    }

    private static async Task<string> WaitForHttpStringAsync(HttpClient client, string path)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        string? lastFailure = null;

        while (DateTime.UtcNow < timeout)
        {
            try
            {
                var response = await client.GetAsync(path);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }

                lastFailure = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException ex)
            {
                lastFailure = ex.Message;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"HTTP endpoint '{path}' did not become ready in time. Last failure: {lastFailure ?? "none"}.");
    }
}

internal sealed record TestStorageProvider(string Name, string Type, string RootPath)
{
    public object ToJsonShape()
    {
        const string fileSystemStorageType = "fileSystem";

        return Type switch
        {
            var type when string.Equals(type, fileSystemStorageType, StringComparison.OrdinalIgnoreCase)
                => new
                {
                    Name,
                    Type,
                    FileSystem = new
                    {
                        RootPath
                    }
                },
            _ => new { Name, Type }
        };
    }
}

internal sealed record TestDomain(string Name, string DefaultStorageProvider, IReadOnlyList<TestMailbox> Mailboxes)
{
    public object ToJsonShape()
    {
        return new
        {
            Name,
            DefaultStorageProvider,
            Mailboxes = Mailboxes.Select(static mailbox => mailbox.ToJsonShape()).ToArray()
        };
    }
}

internal sealed record TestMailbox(string Address, string MailboxId, string? StorageProvider = null)
{
    public object ToJsonShape()
    {
        return new
        {
            Address,
            MailboxId,
            StorageProvider
        };
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
        var process = StartProcess(port, extraEnvironment);
        var runningServer = new RunningServer(process);
        await runningServer.WaitForServerAsync(port);
        return runningServer;
    }

    public static async Task<string> StartExpectFailureAsync(int port, IReadOnlyDictionary<string, string?> extraEnvironment)
    {
        var process = StartProcess(port, extraEnvironment);
        try
        {
            var exitTask = process.WaitForExitAsync();
            var completedTask = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(10)));
            if (completedTask != exitTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch
                {
                    // Best effort cleanup only.
                }

                throw new InvalidOperationException("Expected SMTP server startup to fail, but it kept running.");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            if (process.ExitCode == 0)
            {
                throw new InvalidOperationException("Expected SMTP server startup to fail, but it exited successfully.");
            }

            return $"STDOUT:\n{stdout}\nSTDERR:\n{stderr}";
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Process StartProcess(int port, IReadOnlyDictionary<string, string?> extraEnvironment)
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
        if (!extraEnvironment.ContainsKey("SYMPOSIA_HTTP_PORT"))
        {
            startInfo.Environment["SYMPOSIA_HTTP_PORT"] = GetAvailablePort().ToString();
        }

        foreach (var pair in extraEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start SMTP server process.");
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

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
