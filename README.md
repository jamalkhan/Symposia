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

The `DevSmtp` launch profile sets the local port, TLS certificate path, test credentials, and a repo-local mail drop. If you want different local values, edit [launchSettings.json](/Users/jamal/Projects/Symposia/EmailProvider/SymposiaServer/Properties/launchSettings.json) instead of remembering a long `env ... dotnet run` command.

## Integration Tests

Run the SMTP integration checks with:

```bash
dotnet run --project EmailProvider/SymposiaServer.IntegrationTests/SymposiaServer.IntegrationTests.csproj
```

The integration runner starts `SymposiaServer` on a temporary port, generates a temporary certificate, verifies the secure flow `EHLO -> STARTTLS -> AUTH LOGIN -> MAIL -> RCPT -> DATA`, and also checks failure cases like invalid command ordering and bad authentication.
