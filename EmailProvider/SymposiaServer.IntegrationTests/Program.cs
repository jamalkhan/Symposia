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
            RunInboxAuthApiAsync,
            RunInboxContactsApiAsync,
            RunInboxMailboxWorkflowApiAsync,
            RunInboxOutboundRelayAsync,
            RunMixedRecipientHandlingAsync,
            RunCommandOrderingFailuresAsync,
            RunMessageSizeLimitAsync,
            RunConnectionRateLimitAsync,
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
            ExpectSingle(await client.SendDataAsync(
            [
                "From: Sender Example <sender@example.com>",
                "To: Jamal One <jamal@domain1.com>, Jamal Two <jamal@domain2.com>",
                "Subject: Mailbox Read",
                "Received-SPF: pass client-ip=127.0.0.1; envelope-from=sender@example.com;",
                "Authentication-Results: mx.symposia.test; spf=pass smtp.mailfrom=sender@example.com; dkim=pass header.d=example.com; dmarc=pass action=none",
                "DKIM-Signature: v=1; a=rsa-sha256; d=example.com; s=test; bh=abc; b=xyz;",
                "MIME-Version: 1.0",
                "Content-Type: multipart/alternative; boundary=\"symposia-boundary\"",
                "",
                "--symposia-boundary",
                "Content-Type: text/plain; charset=utf-8",
                "",
                "Logical mailbox storage.",
                "--symposia-boundary",
                "Content-Type: text/html; charset=utf-8",
                "",
                "<p><strong>Logical</strong> mailbox storage.</p>",
                "--symposia-boundary--"
            ]), "250 2.0.0 Ok: queued");
            ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
        }

        var directory = HostingDirectory.Load(hostingConfigPath);
        var readService = new MailboxReadService(directory, new MailboxStorageProviderCatalog(directory, NullLoggerFactory.Instance), NullLogger<MailboxReadService>.Instance);

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

        if (!string.Equals(storedMessage.Metadata.HeaderFrom, "Sender Example <sender@example.com>", StringComparison.Ordinal) ||
            !string.Equals(storedMessage.Metadata.HeaderTo, "Jamal One <jamal@domain1.com>, Jamal Two <jamal@domain2.com>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stored mailbox message metadata did not parse From/To headers as expected.");
        }

        if (!string.Equals(storedMessage.Metadata.PlainTextBody, "Logical mailbox storage.", StringComparison.Ordinal) ||
            !string.Equals(storedMessage.Metadata.HtmlBody, "<p><strong>Logical</strong> mailbox storage.</p>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stored mailbox message metadata did not parse plain text and HTML bodies as expected.");
        }

        if (!storedMessage.Metadata.Headers.Any(static header => string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)) ||
            !storedMessage.Metadata.Headers.Any(static header => string.Equals(header.Name, "From", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Stored mailbox message metadata did not preserve the parsed header collection.");
        }

        if (!string.Equals(storedMessage.Metadata.AuthenticationAwareness.SpfStatus, "pass", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(storedMessage.Metadata.AuthenticationAwareness.DkimStatus, "pass", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(storedMessage.Metadata.AuthenticationAwareness.DmarcStatus, "pass", StringComparison.OrdinalIgnoreCase) ||
            !storedMessage.Metadata.AuthenticationAwareness.HasDkimSignature)
        {
            throw new InvalidOperationException("Stored mailbox message metadata did not capture SPF/DKIM/DMARC awareness as expected.");
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

    private static async Task RunInboxAuthApiAsync()
    {
        var httpPort = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var mailRoot = Path.Combine(tempRoot, "maildrop");
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");
        var accountStorePath = Path.Combine(tempRoot, "accounts.json");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, mailRoot)],
            [
                new TestDomain("symposia.com", "local-default",
                [
                    new TestMailbox("jamal@symposia.com", "jamal"),
                    new TestMailbox("admin@symposia.com", "admin")
                ]),
                new TestDomain("symposia.net", "local-default",
                [
                    new TestMailbox("jamal@symposia.net", "jamal")
                ])
            ]);

        await using var server = await RunningInboxServer.StartAsync(httpPort, new Dictionary<string, string?>
        {
            ["SYMPOSIA_INBOX_HTTP_PORT"] = httpPort.ToString(),
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath,
            ["SYMPOSIA_INBOX_ACCOUNT_STORE_PATH"] = accountStorePath,
            ["SYMPOSIA_INBOX_LOCKOUT_THRESHOLD"] = "3",
            ["SYMPOSIA_INBOX_LOCKOUT_MINUTES"] = "10",
            ["SYMPOSIA_INBOX_EXPOSE_RESET_TOKENS"] = "true"
        });

        var cookies = new CookieContainer();
        using var client = CreateHttpClient(httpPort, cookies);

        var domainsResponse = await client.GetAsync("/api/auth/domains");
        ExpectStatus(domainsResponse.StatusCode, HttpStatusCode.OK, "domains status");
        var domains = JsonNode.Parse(await domainsResponse.Content.ReadAsStringAsync())?.AsArray()
            ?? throw new InvalidOperationException("Hosted domains response could not be parsed.");
        if (domains.Count != 2)
        {
            throw new InvalidOperationException($"Expected 2 hosted domains, found {domains.Count}.");
        }

        var meResponse = await client.GetAsync("/api/auth/me");
        ExpectStatus(meResponse.StatusCode, HttpStatusCode.Unauthorized, "me unauthorized status");

        var invalidDomainResponse = await PostJsonAsync(client, "/api/auth/register", new
        {
            Username = "violet",
            Domain = "unknown.com",
            Password = "testpass123",
            DisplayName = "Violet"
        });
        ExpectStatus(invalidDomainResponse.StatusCode, HttpStatusCode.BadRequest, "invalid domain register status");
        ExpectContains(await invalidDomainResponse.Content.ReadAsStringAsync(), "not hosted by this server");

        var registerResponse = await PostJsonAsync(client, "/api/auth/register", new
        {
            Username = "violet",
            Domain = "symposia.com",
            Password = "testpass123",
            DisplayName = "Violet User"
        });
        ExpectStatus(registerResponse.StatusCode, HttpStatusCode.OK, "register status");
        var registerPayload = JsonNode.Parse(await registerResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Register response could not be parsed.");
        var csrfToken = registerPayload["csrfToken"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Register response did not include a csrf token.");
        var registeredAddress = registerPayload["address"]?.GetValue<string>();
        if (!string.Equals(registeredAddress, "violet@symposia.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected registered address violet@symposia.com, got '{registeredAddress}'.");
        }

        var updatedHostingConfig = await File.ReadAllTextAsync(hostingConfigPath);
        ExpectContains(updatedHostingConfig, "\"address\": \"violet@symposia.com\"");
        var updatedAccountsConfig = await File.ReadAllTextAsync(accountStorePath);
        ExpectContains(updatedAccountsConfig, "\"address\": \"violet@symposia.com\"");

        var duplicateResponse = await PostJsonAsync(client, "/api/auth/register", new
        {
            Username = "violet",
            Domain = "symposia.com",
            Password = "testpass123",
            DisplayName = "Violet User"
        });
        ExpectStatus(duplicateResponse.StatusCode, HttpStatusCode.BadRequest, "duplicate register status");
        ExpectContains(await duplicateResponse.Content.ReadAsStringAsync(), "already exists");

        var authedMeResponse = await client.GetAsync("/api/auth/me");
        ExpectStatus(authedMeResponse.StatusCode, HttpStatusCode.OK, "me authorized status");
        var authedMePayload = await authedMeResponse.Content.ReadAsStringAsync();
        ExpectContains(authedMePayload, "violet@symposia.com");

        var logoutResponse = await PostWithCsrfAsync(client, "/api/auth/logout", GetCsrfToken(authedMePayload));
        ExpectStatus(logoutResponse.StatusCode, HttpStatusCode.NoContent, "logout status");

        var wrongPasswordResponse = await PostJsonAsync(client, "/api/auth/login", new
        {
            EmailAddress = "violet@symposia.com",
            Password = "wrongpass"
        });
        ExpectStatus(wrongPasswordResponse.StatusCode, HttpStatusCode.Unauthorized, "wrong password login status");

        var secondWrongPasswordResponse = await PostJsonAsync(client, "/api/auth/login", new
        {
            EmailAddress = "violet@symposia.com",
            Password = "wrongpass"
        });
        ExpectStatus(secondWrongPasswordResponse.StatusCode, HttpStatusCode.Unauthorized, "second wrong password login status");

        var lockedPasswordResponse = await PostJsonAsync(client, "/api/auth/login", new
        {
            EmailAddress = "violet@symposia.com",
            Password = "wrongpass"
        });
        ExpectStatus(lockedPasswordResponse.StatusCode, HttpStatusCode.Locked, "locked password login status");

        var resetRequestResponse = await PostJsonAsync(client, "/api/auth/password-reset/request", new
        {
            EmailAddress = "violet@symposia.com"
        });
        ExpectStatus(resetRequestResponse.StatusCode, HttpStatusCode.OK, "password reset request status");
        var resetRequestPayload = JsonNode.Parse(await resetRequestResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Password reset request response could not be parsed.");
        var resetToken = resetRequestPayload["resetToken"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected exposed password reset token for integration test.");

        var resetConfirmResponse = await PostJsonAsync(client, "/api/auth/password-reset/confirm", new
        {
            Token = resetToken,
            NewPassword = "renewedpass123"
        });
        ExpectStatus(resetConfirmResponse.StatusCode, HttpStatusCode.NoContent, "password reset confirm status");

        var oldPasswordResponse = await PostJsonAsync(client, "/api/auth/login", new
        {
            EmailAddress = "violet@symposia.com",
            Password = "testpass123"
        });
        ExpectStatus(oldPasswordResponse.StatusCode, HttpStatusCode.Unauthorized, "old password login status");

        var loginResponse = await PostJsonAsync(client, "/api/auth/login", new
        {
            EmailAddress = "violet@symposia.com",
            Password = "renewedpass123"
        });
        ExpectStatus(loginResponse.StatusCode, HttpStatusCode.OK, "login status");
        ExpectContains(await loginResponse.Content.ReadAsStringAsync(), "violet@symposia.com");
    }

    private static async Task RunInboxContactsApiAsync()
    {
        var httpPort = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var mailRoot = Path.Combine(tempRoot, "maildrop");
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");
        var accountStorePath = Path.Combine(tempRoot, "accounts.json");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, mailRoot)],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await using var server = await RunningInboxServer.StartAsync(httpPort, new Dictionary<string, string?>
        {
            ["SYMPOSIA_INBOX_HTTP_PORT"] = httpPort.ToString(),
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath,
            ["SYMPOSIA_INBOX_ACCOUNT_STORE_PATH"] = accountStorePath
        });

        var cookies = new CookieContainer();
        using var client = CreateHttpClient(httpPort, cookies);

        var registerResponse = await PostJsonAsync(client, "/api/auth/register", new
        {
            Username = "jamal",
            Domain = "symposia.com",
            Password = "testpass123",
            DisplayName = "Jamal"
        });
        await ExpectSuccessAsync(registerResponse, "register contact test account");
        var csrfToken = GetCsrfToken(await registerResponse.Content.ReadAsStringAsync());

        var createContactResponse = await PostJsonWithCsrfAsync(client, "/api/contacts", new
        {
            DisplayName = "Ada Lovelace",
            EmailAddress = "ada@example.com"
        }, csrfToken);
        ExpectStatus(createContactResponse.StatusCode, HttpStatusCode.OK, "create contact status");
        var contact = JsonNode.Parse(await createContactResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Create contact response could not be parsed.");
        var contactId = contact["contactId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Contact response did not include an id.");

        var filteredContactsResponse = await client.GetAsync("/api/contacts?q=ada");
        ExpectStatus(filteredContactsResponse.StatusCode, HttpStatusCode.OK, "contacts query status");
        var filteredContacts = JsonNode.Parse(await filteredContactsResponse.Content.ReadAsStringAsync())?.AsArray()
            ?? throw new InvalidOperationException("Filtered contacts response could not be parsed.");
        if (filteredContacts.Count != 1)
        {
            throw new InvalidOperationException($"Expected one filtered contact, found {filteredContacts.Count}.");
        }

        var updateContactResponse = await PostJsonWithCsrfAsync(client, "/api/contacts", new
        {
            ContactId = contactId,
            DisplayName = "Ada Byron",
            EmailAddress = "ada@example.com"
        }, csrfToken);
        ExpectStatus(updateContactResponse.StatusCode, HttpStatusCode.OK, "update contact status");
        ExpectContains(await updateContactResponse.Content.ReadAsStringAsync(), "Ada Byron");

        var deleteResponse = await DeleteWithCsrfAsync(client, $"/api/contacts/{contactId}", csrfToken);
        ExpectStatus(deleteResponse.StatusCode, HttpStatusCode.NoContent, "delete contact status");

        var finalContactsResponse = await client.GetAsync("/api/contacts");
        ExpectStatus(finalContactsResponse.StatusCode, HttpStatusCode.OK, "final contacts status");
        var finalContacts = JsonNode.Parse(await finalContactsResponse.Content.ReadAsStringAsync())?.AsArray()
            ?? throw new InvalidOperationException("Final contacts response could not be parsed.");
        if (finalContacts.Count != 0)
        {
            throw new InvalidOperationException($"Expected zero contacts after deletion, found {finalContacts.Count}.");
        }
    }

    private static async Task RunInboxMailboxWorkflowApiAsync()
    {
        var httpPort = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var mailRoot = Path.Combine(tempRoot, "maildrop");
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");
        var accountStorePath = Path.Combine(tempRoot, "accounts.json");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, mailRoot)],
            [new TestDomain("symposia.com", "local-default",
            [
                new TestMailbox("jamal@symposia.com", "jamal"),
                new TestMailbox("admin@symposia.com", "admin")
            ])]);

        await using var server = await RunningInboxServer.StartAsync(httpPort, new Dictionary<string, string?>
        {
            ["SYMPOSIA_INBOX_HTTP_PORT"] = httpPort.ToString(),
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath,
            ["SYMPOSIA_INBOX_ACCOUNT_STORE_PATH"] = accountStorePath
        });

        var senderCookies = new CookieContainer();
        using var senderClient = CreateHttpClient(httpPort, senderCookies);
        var adminCookies = new CookieContainer();
        using var adminClient = CreateHttpClient(httpPort, adminCookies);

        var senderRegisterResponse = await PostJsonAsync(senderClient, "/api/auth/register", new
        {
            Username = "jamal",
            Domain = "symposia.com",
            Password = "testpass123",
            DisplayName = "Jamal"
        });
        await ExpectSuccessAsync(senderRegisterResponse, "register sender account");
        var senderCsrf = GetCsrfToken(await senderRegisterResponse.Content.ReadAsStringAsync());

        var composeEmptyRecipients = await PostJsonWithCsrfAsync(senderClient, "/api/mailbox/compose", new
        {
            Subject = "No recipients",
            To = "",
            PlainTextBody = "This should fail."
        }, senderCsrf);
        ExpectStatus(composeEmptyRecipients.StatusCode, HttpStatusCode.BadRequest, "compose invalid status");
        ExpectContains(await composeEmptyRecipients.Content.ReadAsStringAsync(), "At least one recipient is required");

        var composeResponse = await PostJsonWithCsrfAsync(senderClient, "/api/mailbox/compose", new
        {
            Subject = "Inbox workflow",
            To = "admin@symposia.com, outside@example.net",
            PlainTextBody = "Hello from the inbox integration test."
        }, senderCsrf);
        ExpectStatus(composeResponse.StatusCode, HttpStatusCode.OK, "compose status");
        var composePayload = JsonNode.Parse(await composeResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Compose response could not be parsed.");
        var sentMessageId = composePayload["sentMessageId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Compose response did not include a sent message id.");
        ExpectEqual(composePayload["deliveredLocalCount"]?.GetValue<int>(), 1, "local delivered count");
        ExpectEqual(composePayload["queuedExternalCount"]?.GetValue<int>(), 1, "queued external count");

        var sentPageResponse = await senderClient.GetAsync("/api/mailbox/messages/page?folder=sent&page=1&pageSize=10");
        if (!sentPageResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Expected sender sent page request to succeed, got HTTP {(int)sentPageResponse.StatusCode}: {await sentPageResponse.Content.ReadAsStringAsync()}");
        }

        var sentPageBody = await sentPageResponse.Content.ReadAsStringAsync();
        var sentPagePayload = JsonNode.Parse(sentPageBody)?.AsObject()
            ?? throw new InvalidOperationException("Sent page response could not be parsed.");
        if (sentPagePayload["totalCount"]?.GetValue<int>() != 1)
        {
            throw new InvalidOperationException($"Expected sender sent page totalCount to be 1, got '{sentPagePayload["totalCount"]?.ToJsonString() ?? "null"}'. Body: {sentPageBody}");
        }

        var sentMessagesResponse = await senderClient.GetAsync("/api/mailbox/messages?folder=sent");
        ExpectStatus(sentMessagesResponse.StatusCode, HttpStatusCode.OK, "sent messages status");
        var sentMessages = JsonNode.Parse(await sentMessagesResponse.Content.ReadAsStringAsync())?.AsArray()
            ?? throw new InvalidOperationException("Sent messages response could not be parsed.");
        if (sentMessages.Count != 1)
        {
            throw new InvalidOperationException($"Expected one sent message, found {sentMessages.Count}.");
        }

        var sentMessage = sentMessages[0]?.AsObject()
            ?? throw new InvalidOperationException("Sent message payload could not be parsed.");
        if (!string.Equals(sentMessage["messageId"]?.GetValue<string>(), sentMessageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Sent message listing did not include the composed message.");
        }

        var adminRegisterResponse = await PostJsonAsync(adminClient, "/api/auth/register", new
        {
            Username = "admin",
            Domain = "symposia.com",
            Password = "adminpass123",
            DisplayName = "Admin"
        });
        await ExpectSuccessAsync(adminRegisterResponse, "register admin account");
        var adminCsrf = GetCsrfToken(await adminRegisterResponse.Content.ReadAsStringAsync());

        var bootstrapResponse = await adminClient.GetAsync("/api/mailbox/bootstrap");
        ExpectStatus(bootstrapResponse.StatusCode, HttpStatusCode.OK, "bootstrap status");
        var bootstrap = JsonNode.Parse(await bootstrapResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Bootstrap response could not be parsed.");
        ExpectEqual(bootstrap["counts"]?["inbox"]?.GetValue<int>(), 1, "admin inbox count");
        var recentMessages = bootstrap["recentMessages"]?["items"]?.AsArray()
            ?? throw new InvalidOperationException("Bootstrap response missing recentMessages.");
        if (recentMessages.Count != 1)
        {
            throw new InvalidOperationException($"Expected one recent inbox message, found {recentMessages.Count}.");
        }

        var adminMessageId = recentMessages[0]?["messageId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Recent inbox message did not include a message id.");

        var searchResponse = await adminClient.GetAsync("/api/mailbox/messages?folder=inbox&q=workflow");
        ExpectStatus(searchResponse.StatusCode, HttpStatusCode.OK, "search status");
        var searchResults = JsonNode.Parse(await searchResponse.Content.ReadAsStringAsync())?.AsArray()
            ?? throw new InvalidOperationException("Search response could not be parsed.");
        if (searchResults.Count != 1)
        {
            throw new InvalidOperationException($"Expected one search result, found {searchResults.Count}.");
        }

        var detailResponse = await adminClient.GetAsync($"/api/mailbox/messages/{adminMessageId}");
        ExpectStatus(detailResponse.StatusCode, HttpStatusCode.OK, "message detail status");
        var detail = JsonNode.Parse(await detailResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Message detail response could not be parsed.");
        ExpectContains(detail["plainTextBody"]?.GetValue<string>() ?? string.Empty, "Hello from the inbox integration test.");
        var threadId = detail["threadId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Message detail did not include thread id.");

        var pageResponse = await adminClient.GetAsync("/api/mailbox/messages/page?folder=inbox&page=1&pageSize=10");
        ExpectStatus(pageResponse.StatusCode, HttpStatusCode.OK, "paged messages status");
        var pagePayload = JsonNode.Parse(await pageResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Paged messages response could not be parsed.");
        ExpectEqual(pagePayload["totalCount"]?.GetValue<int>(), 1, "paged inbox total count");

        ExpectStatus((await PostWithCsrfAsync(adminClient, $"/api/mailbox/messages/{adminMessageId}/read", adminCsrf)).StatusCode, HttpStatusCode.NoContent, "mark read status");
        var readDetail = JsonNode.Parse(await (await adminClient.GetAsync($"/api/mailbox/messages/{adminMessageId}")).Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Read detail response could not be parsed.");
        if (!readDetail["isRead"]!.GetValue<bool>())
        {
            throw new InvalidOperationException("Expected message to be marked read.");
        }

        ExpectStatus((await PostWithCsrfAsync(adminClient, $"/api/mailbox/messages/{adminMessageId}/unread", adminCsrf)).StatusCode, HttpStatusCode.NoContent, "mark unread status");
        var unreadDetail = JsonNode.Parse(await (await adminClient.GetAsync($"/api/mailbox/messages/{adminMessageId}")).Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Unread detail response could not be parsed.");
        if (unreadDetail["isRead"]!.GetValue<bool>())
        {
            throw new InvalidOperationException("Expected message to be marked unread.");
        }

        ExpectStatus((await PostJsonWithCsrfAsync(adminClient, $"/api/mailbox/messages/{adminMessageId}/labels", new
        {
            Labels = new[] { "priority", "customer" }
        }, adminCsrf)).StatusCode, HttpStatusCode.NoContent, "update labels status");
        ExpectStatus((await PostJsonWithCsrfAsync(adminClient, $"/api/mailbox/messages/{adminMessageId}/star", new
        {
            IsStarred = true
        }, adminCsrf)).StatusCode, HttpStatusCode.NoContent, "update star status");

        var labeledDetail = JsonNode.Parse(await (await adminClient.GetAsync($"/api/mailbox/messages/{adminMessageId}")).Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Labeled detail response could not be parsed.");
        ExpectEqual(labeledDetail["isStarred"]?.GetValue<bool>(), true, "starred flag");
        var labels = labeledDetail["labels"]?.AsArray() ?? throw new InvalidOperationException("Message detail did not include labels.");
        if (labels.Count != 2)
        {
            throw new InvalidOperationException($"Expected two labels on message, found {labels.Count}.");
        }

        var labeledSearch = await adminClient.GetAsync("/api/mailbox/messages/page?folder=inbox&label=priority");
        ExpectStatus(labeledSearch.StatusCode, HttpStatusCode.OK, "label search status");
        var labeledPage = JsonNode.Parse(await labeledSearch.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Label search response could not be parsed.");
        ExpectEqual(labeledPage["totalCount"]?.GetValue<int>(), 1, "labeled inbox total count");

        var replyResponse = await PostJsonWithCsrfAsync(senderClient, "/api/mailbox/compose", new
        {
            Subject = "RE: Inbox workflow",
            To = "admin@symposia.com",
            PlainTextBody = "Following up on the same thread.",
            ReplyToMessageId = adminMessageId
        }, senderCsrf);
        ExpectStatus(replyResponse.StatusCode, HttpStatusCode.OK, "reply compose status");

        await WaitForConditionAsync(async () =>
        {
            var response = await adminClient.GetAsync("/api/mailbox/threads?folder=inbox&page=1&pageSize=10");
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
            return payload?["items"]?.AsArray()?.Count == 1
                && payload["items"]?[0]?["messageCount"]?.GetValue<int>() == 2;
        }, "admin inbox thread list to show two messages");

        var threadsResponse = await adminClient.GetAsync("/api/mailbox/threads?folder=inbox&page=1&pageSize=10");
        ExpectStatus(threadsResponse.StatusCode, HttpStatusCode.OK, "thread page status");
        var threadsPayload = JsonNode.Parse(await threadsResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Thread page response could not be parsed.");
        var threadItems = threadsPayload["items"]?.AsArray()
            ?? throw new InvalidOperationException("Thread page did not include items.");
        if (threadItems.Count != 1)
        {
            throw new InvalidOperationException($"Expected one thread item, found {threadItems.Count}.");
        }
        var threadSummary = threadItems[0]?.AsObject()
            ?? throw new InvalidOperationException("Thread summary could not be parsed.");
        if (!string.Equals(threadSummary["threadId"]?.GetValue<string>(), threadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Thread summary did not match the expected thread id.");
        }
        ExpectEqual(threadSummary["messageCount"]?.GetValue<int>(), 2, "thread message count");

        var threadDetailResponse = await adminClient.GetAsync($"/api/mailbox/threads/{threadId}");
        ExpectStatus(threadDetailResponse.StatusCode, HttpStatusCode.OK, "thread detail status");
        var threadDetail = JsonNode.Parse(await threadDetailResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Thread detail response could not be parsed.");
        var threadMessages = threadDetail["messages"]?.AsArray()
            ?? throw new InvalidOperationException("Thread detail did not include messages.");
        if (threadMessages.Count != 2)
        {
            throw new InvalidOperationException($"Expected two messages in thread detail, found {threadMessages.Count}.");
        }

        ExpectStatus((await PostWithCsrfAsync(adminClient, $"/api/mailbox/messages/{adminMessageId}/delete", adminCsrf)).StatusCode, HttpStatusCode.NoContent, "delete status");
        var trashResponse = await adminClient.GetAsync("/api/mailbox/messages?folder=trash");
        ExpectStatus(trashResponse.StatusCode, HttpStatusCode.OK, "trash messages status");
        ExpectContains(await trashResponse.Content.ReadAsStringAsync(), adminMessageId);

        ExpectStatus((await PostWithCsrfAsync(adminClient, $"/api/mailbox/messages/{adminMessageId}/restore", adminCsrf)).StatusCode, HttpStatusCode.NoContent, "restore status");
        var restoredInbox = await adminClient.GetAsync("/api/mailbox/messages?folder=inbox");
        ExpectStatus(restoredInbox.StatusCode, HttpStatusCode.OK, "restored inbox status");
        ExpectContains(await restoredInbox.Content.ReadAsStringAsync(), adminMessageId);

        var outboundPath = Path.Combine(mailRoot, "outbound", "pending");
        var outboundQueueFiles = Directory.GetFiles(outboundPath, "*.json", SearchOption.TopDirectoryOnly);
        if (outboundQueueFiles.Length != 1)
        {
            throw new InvalidOperationException($"Expected one outbound queue file, found {outboundQueueFiles.Length}.");
        }

        var outboundQueueJson = await File.ReadAllTextAsync(outboundQueueFiles[0]);
        ExpectContains(outboundQueueJson, "outside@example.net");
    }

    private static async Task RunInboxOutboundRelayAsync()
    {
        var relaySmtpPort = GetFreePort();
        var inboxHttpPort = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var inboxMailRoot = Path.Combine(tempRoot, "inbox-maildrop");
        var relayMailRoot = Path.Combine(tempRoot, "relay-maildrop");
        var inboxHostingConfigPath = Path.Combine(tempRoot, "inbox-mailboxes.json");
        var relayHostingConfigPath = Path.Combine(tempRoot, "relay-mailboxes.json");
        var accountStorePath = Path.Combine(tempRoot, "accounts.json");

        await WriteHostingConfigAsync(
            inboxHostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, inboxMailRoot)],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await WriteHostingConfigAsync(
            relayHostingConfigPath,
            [new TestStorageProvider("relay-default", FileSystemStorageType, relayMailRoot)],
            [new TestDomain("relay-target.net", "relay-default", [new TestMailbox("outside@relay-target.net", "outside")])]);

        await using var relayServer = await RunningServer.StartAsync(relaySmtpPort, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = relayHostingConfigPath,
            ["SYMPOSIA_HTTP_PORT"] = GetFreePort().ToString()
        });

        await using var inboxServer = await RunningInboxServer.StartAsync(inboxHttpPort, new Dictionary<string, string?>
        {
            ["SYMPOSIA_INBOX_HTTP_PORT"] = inboxHttpPort.ToString(),
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = inboxHostingConfigPath,
            ["SYMPOSIA_INBOX_ACCOUNT_STORE_PATH"] = accountStorePath,
            ["SYMPOSIA_INBOX_OUTBOUND_RELAY_HOST"] = "127.0.0.1",
            ["SYMPOSIA_INBOX_OUTBOUND_RELAY_PORT"] = relaySmtpPort.ToString(),
            ["SYMPOSIA_INBOX_OUTBOUND_POLL_SECONDS"] = "1"
        });

        var cookies = new CookieContainer();
        using var client = CreateHttpClient(inboxHttpPort, cookies);

        var registerResponse = await PostJsonAsync(client, "/api/auth/register", new
        {
            Username = "jamal",
            Domain = "symposia.com",
            Password = "testpass123",
            DisplayName = "Jamal"
        });
        await ExpectSuccessAsync(registerResponse, "register outbound relay account");
        var csrfToken = GetCsrfToken(await registerResponse.Content.ReadAsStringAsync());

        var composeResponse = await PostJsonWithCsrfAsync(client, "/api/mailbox/compose", new
        {
            Subject = "Relay this externally",
            To = "outside@relay-target.net",
            PlainTextBody = "Outbound relay integration message."
        }, csrfToken);
        ExpectStatus(composeResponse.StatusCode, HttpStatusCode.OK, "outbound relay compose status");

        await WaitForConditionAsync(
            () => Task.FromResult(Directory.Exists(Path.Combine(inboxMailRoot, "outbound", "sent"))
                && Directory.GetFiles(Path.Combine(inboxMailRoot, "outbound", "sent"), "*.json", SearchOption.TopDirectoryOnly).Length == 1),
            "outbound relay queue to be marked sent");

        await WaitForConditionAsync(
            () => Task.FromResult(Directory.Exists(Path.Combine(relayMailRoot, "mailboxes", "outside", "messages"))
                && Directory.GetFiles(Path.Combine(relayMailRoot, "mailboxes", "outside", "messages"), "*.eml", SearchOption.TopDirectoryOnly).Length == 1),
            "relayed message to arrive in relay target mailbox");

        var relayedMessagePath = Directory.GetFiles(Path.Combine(relayMailRoot, "mailboxes", "outside", "messages"), "*.eml", SearchOption.TopDirectoryOnly)[0];
        var relayedMessage = await File.ReadAllTextAsync(relayedMessagePath);
        ExpectContains(relayedMessage, "Subject: Relay this externally");
        ExpectContains(relayedMessage, "Outbound relay integration message.");
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
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@example.com>"), "554 5.7.1 Relay access denied");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
    }

    private static async Task RunMessageSizeLimitAsync()
    {
        var port = GetFreePort();
        var tempRoot = CreateTempDirectory();
        var hostingConfigPath = Path.Combine(tempRoot, "mailboxes.json");
        var mailRoot = Path.Combine(tempRoot, "maildrop");

        await WriteHostingConfigAsync(
            hostingConfigPath,
            [new TestStorageProvider("local-default", FileSystemStorageType, mailRoot)],
            [new TestDomain("symposia.com", "local-default", [new TestMailbox("jamal@symposia.com", "jamal")])]);

        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath,
            ["SYMPOSIA_SMTP_MAX_MESSAGE_SIZE_BYTES"] = "1024"
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250 HELP");
        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("DATA"), "354 End data with <CR><LF>.<CR><LF>");
        ExpectSingle(await client.SendDataAsync(
        [
            "Subject: Too Large",
            "",
            new string('A', 2000)
        ]), "552 5.3.4 Message size exceeds fixed maximum message size");
        ExpectSingle(await client.SendCommandAsync("QUIT"), "221 2.0.0 Bye");
        ExpectMailboxMessagePersisted(mailRoot, "jamal", 0);
    }

    private static async Task RunConnectionRateLimitAsync()
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
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath,
            ["SYMPOSIA_SMTP_MAX_CONNECTIONS_PER_IP_PER_MINUTE"] = "4"
        });

        using var client1 = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client1.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectSingle(await client1.SendCommandAsync("QUIT"), "221 2.0.0 Bye");

        using var client2 = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client2.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectSingle(await client2.SendCommandAsync("QUIT"), "221 2.0.0 Bye");

        using var client3 = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client3.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectSingle(await client3.SendCommandAsync("QUIT"), "221 2.0.0 Bye");

        using var client4 = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client4.ReadResponseAsync(), "421 4.7.0 Connection rate limit exceeded");
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

        var retryRoot = Path.Combine(tempRoot, "retry-queue");
        await using var server = await RunningServer.StartAsync(port, new Dictionary<string, string?>
        {
            ["SYMPOSIA_SMTP_SERVER_NAME"] = "localhost",
            ["SYMPOSIA_SMTP_HOSTING_CONFIG"] = hostingConfigPath,
            ["SYMPOSIA_SMTP_TLS_CERT_PATH"] = certPath,
            ["SYMPOSIA_SMTP_TLS_CERT_PASSWORD"] = "symposia-dev-pass",
            ["SYMPOSIA_SMTP_RETRY_QUEUE_ROOT"] = retryRoot
        });

        using var client = await SmtpTestClient.ConnectAsync(port);
        ExpectSingle(await client.ReadResponseAsync(), "220 localhost ESMTP Ready");
        ExpectContains(await client.SendCommandAsync("EHLO localhost"), "250-STARTTLS");
        ExpectSingle(await client.SendCommandAsync("MAIL FROM:<sender@example.com>"), "250 2.1.0 Ok");
        ExpectSingle(await client.SendCommandAsync("RCPT TO:<jamal@symposia.com>"), "250 2.1.5 Ok");
        ExpectSingle(await client.SendCommandAsync("DATA"), "354 End data with <CR><LF>.<CR><LF>");
        ExpectSingle(await client.SendDataAsync(["Subject: Failure", "", "filesystem should fail"]), "250 2.0.0 Ok: queued for retry");
        var retryFiles = Directory.GetFiles(Path.Combine(retryRoot, "pending"), "*.json", SearchOption.TopDirectoryOnly);
        if (retryFiles.Length != 1)
        {
            throw new InvalidOperationException($"Expected one retry queue file, found {retryFiles.Length}.");
        }
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
        ExpectSingle(await client.SendDataAsync(["Subject: Unsupported", "", "unsupported provider should fail"]), "554 5.3.0 Message rejected");
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
            if (expectedCount == 0)
            {
                return;
            }

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

    private static void ExpectStatus(HttpStatusCode actual, HttpStatusCode expected, string label)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected {label} to be '{expected}', got '{actual}'.");
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

    private static async Task WaitForConditionAsync(Func<Task<bool>> predicate, string description)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < timeout)
        {
            if (await predicate())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private static HttpClient CreateHttpClient(int port, CookieContainer? cookies = null)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies ?? new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object payload)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        return client.PostAsync(path, content);
    }

    private static Task<HttpResponseMessage> PostJsonWithCsrfAsync(HttpClient client, string path, object payload, string csrfToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Symposia-Csrf", csrfToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostWithCsrfAsync(HttpClient client, string path, string csrfToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Symposia-Csrf", csrfToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteWithCsrfAsync(HttpClient client, string path, string csrfToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("X-Symposia-Csrf", csrfToken);
        return client.SendAsync(request);
    }

    private static string GetCsrfToken(string responseBody)
    {
        var payload = JsonNode.Parse(responseBody)?.AsObject()
            ?? throw new InvalidOperationException("Expected JSON object response with csrf token.");
        return payload["csrfToken"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Response did not include csrfToken.");
    }

    private static async Task ExpectSuccessAsync(HttpResponseMessage response, string label)
    {
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Expected {label} to succeed, got HTTP {(int)response.StatusCode}: {responseBody}");
        }
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

internal sealed class RunningInboxServer : IAsyncDisposable
{
    private readonly Process _process;

    private RunningInboxServer(Process process)
    {
        _process = process;
    }

    public static async Task<RunningInboxServer> StartAsync(int httpPort, IReadOnlyDictionary<string, string?> extraEnvironment)
    {
        var process = StartProcess(extraEnvironment);
        var server = new RunningInboxServer(process);
        await server.WaitForServerAsync(httpPort);
        return server;
    }

    private static Process StartProcess(IReadOnlyDictionary<string, string?> extraEnvironment)
    {
        var projectRoot = GetProjectRoot();
        var inboxDll = Path.Combine(projectRoot, "EmailProvider", "SymposiaInboxWeb", "bin", "Debug", "net9.0", "SymposiaInboxWeb.dll");

        var startInfo = new ProcessStartInfo("dotnet", $"\"{inboxDll}\"")
        {
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var pair in extraEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start inbox server process.");
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

    private async Task WaitForServerAsync(int httpPort)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{httpPort}")
        };

        var timeout = DateTime.UtcNow.AddSeconds(10);
        string? lastFailure = null;

        while (DateTime.UtcNow < timeout)
        {
            if (_process.HasExited)
            {
                var stdout = await _process.StandardOutput.ReadToEndAsync();
                var stderr = await _process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Inbox server exited unexpectedly.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            }

            try
            {
                var response = await client.GetAsync("/api/health");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastFailure = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException ex)
            {
                lastFailure = ex.Message;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Inbox server did not become ready in time. Last failure: {lastFailure ?? "none"}.");
    }

    private static string GetProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
