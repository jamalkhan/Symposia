# Symposia

## Local SMTP Development

Generate a local development certificate once:

```bash
./EmailProvider/scripts/setup-dev-smtp-cert.sh
```

Run the SMTP server with the built-in local development profile:

```bash
dotnet run --project EmailProvider/SymposiaServer/SymposiaServer.csproj --launch-profile DevSmtp
```

The `DevSmtp` launch profile sets the SMTP port, dashboard HTTP/HTTPS ports, TLS certificate path, test credentials, and the hosting/storage config path. If you want different local values, edit [launchSettings.json](/Users/jamal/Projects/Symposia/EmailProvider/SymposiaServer/Properties/launchSettings.json) instead of remembering a long `env ... dotnet run` command.

Hosted domains, mailbox routes, and storage providers are configured in [mailboxes.json](/Users/jamal/Projects/Symposia/EmailProvider/SymposiaServer/Config/mailboxes.json). The server rejects unknown domains and unknown local recipients at `RCPT TO`, and mailbox-level storage overrides win over the domain default. You can override the config path with `SYMPOSIA_SMTP_HOSTING_CONFIG` or the legacy `SYMPOSIA_SMTP_RECIPIENT_CONFIG`.

The current storage pattern is a strategy-plus-factory design:

- mailbox routing is resolved from the hosting config
- a storage provider factory materializes concrete provider implementations
- `DATA` delivery groups routed recipients by logical mailbox and hands each mailbox delivery to the selected provider

The working storage implementation today is the filesystem provider, which writes to:

```text
<root>/mailboxes/<mailboxId>/messages/<messageId>.eml
<root>/mailboxes/<mailboxId>/messages/<messageId>.json
<root>/addresses/<domain>/<email_inbox>/pointer.json
```

The raw `.eml` file is the canonical stored message for the logical mailbox. The adjacent JSON file records delivery metadata such as routed addresses and envelope details, and each address pointer records which logical mailbox owns that address. This keeps aliases and multi-domain mailboxes from duplicating the same message body on disk.

The server now also has a mailbox read abstraction for inbox-facing code. `MailboxReadService` resolves configured mailbox bindings by `mailboxId`, lists logical messages for that mailbox, and loads a stored message with metadata plus raw source without requiring callers to know anything about SMTP sessions or filesystem layout.

## Dashboard And REST API

The same `SymposiaServer` process now hosts:

- SMTP on `SYMPOSIA_SMTP_PORT`
- an HTTP dashboard on `SYMPOSIA_HTTP_PORT`
- an HTTPS dashboard/API on `SYMPOSIA_HTTPS_PORT` when a TLS certificate is configured

By default, the dashboard reuses the SMTP TLS certificate settings. You can override them with:

- `SYMPOSIA_HTTP_TLS_CERT_PATH`
- `SYMPOSIA_HTTP_TLS_CERT_PASSWORD`

The dashboard is a simple HTML page served from the root path:

- [http://localhost:5080](http://localhost:5080)
- [https://localhost:5443](https://localhost:5443) when the local dev certificate is present

REST data is exposed through .NET Web API at:

- `GET /api/dashboard/summary`

The dashboard polls that API every 5 seconds by default, and the page includes a slider that lets you adjust the interval from 1 second to 2 minutes.

The config already understands additional provider types for AWS S3, Azure Files, GCP, and Snowflake, but those adapters are currently explicit placeholders until we wire SDKs or emulator-backed implementations for them.

## Integration Tests

Run the SMTP integration checks with:

```bash
dotnet run --project EmailProvider/SymposiaServer.IntegrationTests/SymposiaServer.IntegrationTests.csproj
```

The integration runner starts `SymposiaServer` on a temporary port, generates a temporary certificate, verifies the secure flow `EHLO -> STARTTLS -> AUTH LOGIN -> MAIL -> RCPT -> DATA`, checks failure cases like invalid command ordering and bad authentication, and exercises mailbox storage routing across domain defaults and mailbox-specific overrides.
