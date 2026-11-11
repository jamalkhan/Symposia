using Microsoft.Extensions.Logging.Abstractions;
using Symposia.Network.Node.Alerts;
using Symposia.NodeHost.Alerts;
using Symposia.NodeHost.Settings;
using Symposia.NodeHost.Ui;

namespace Symposia.NodeHost.Tests;

/// <summary>Operator Alerts state, email-on-change, damping, reminders, retries, and settings (issue #196).</summary>
public sealed class OperatorAlertsTests : IDisposable
{
    private const string Node = "0x1234567890abcdef1234567890abcdef12345678";
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new();
    private readonly FakeTransport _mail = new();
    private readonly HostSettings _settings = new() { Alerts = { Email = "ops@example.com", SmtpHost = "smtp.example.com" } };

    private HostPaths Paths => new(_dir.Path);

    public void Dispose() => _dir.Dispose();

    private OperatorAlerts NewAlerts() => new(Paths.AlertsState, NullLogger.Instance, _time);

    private AlertNotifier Notifier(OperatorAlerts alerts) =>
        new(alerts, () => _settings, new SecretStore(Paths), _mail, Node, () => null, NullLogger.Instance, _time);

    private async Task Advance(AlertNotifier notifier, TimeSpan by)
    {
        _time.Now += by;
        await notifier.TickAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StateChanges_AreEmailedOnce_AfterDamping()
    {
        var alerts = NewAlerts();
        var notifier = Notifier(alerts);

        alerts.Raise(AlertKeys.StorageDiskLow, AlertSeverity.Warning, "Low disk space", "3.5 GB free");
        await Advance(notifier, TimeSpan.FromSeconds(30));
        Assert.Empty(_mail.Sent);

        await Advance(notifier, TimeSpan.FromSeconds(31));
        Assert.Single(_mail.Sent);
        Assert.Equal("[Symposia 0x1234…5678] WARNING: Low disk space", _mail.Sent[0].Subject);

        // A continuing Warning is never repeated.
        alerts.Raise(AlertKeys.StorageDiskLow, AlertSeverity.Warning, "Low disk space", "3.5 GB free");
        await Advance(notifier, TimeSpan.FromDays(3));
        Assert.Single(_mail.Sent);

        alerts.Raise(AlertKeys.StorageDiskLow, AlertSeverity.Error, "Low disk space", "1.9 GB free");
        await Advance(notifier, TimeSpan.FromSeconds(61));
        Assert.StartsWith("[Symposia 0x1234…5678] ERROR:", _mail.Sent[1].Subject);

        alerts.Clear(AlertKeys.StorageDiskLow);
        await Advance(notifier, TimeSpan.FromSeconds(61));
        Assert.Equal(3, _mail.Sent.Count);
        Assert.Contains("RESOLVED", _mail.Sent[2].Subject);
        Assert.Empty(alerts.Active);
    }

    [Fact]
    public async Task RaiseAndClear_WithinDamping_SendsNothing()
    {
        var alerts = NewAlerts();
        var notifier = Notifier(alerts);

        alerts.Raise("k", AlertSeverity.Warning, "t", "d");
        await Advance(notifier, TimeSpan.FromSeconds(20));
        alerts.Clear("k");
        await Advance(notifier, TimeSpan.FromMinutes(5));

        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task EscalationInsideDamping_SendsOnlyTheFinalState()
    {
        var alerts = NewAlerts();
        var notifier = Notifier(alerts);

        alerts.Raise("k", AlertSeverity.Warning, "t", "d");
        await Advance(notifier, TimeSpan.FromSeconds(40));
        alerts.Raise("k", AlertSeverity.Error, "t", "d");
        await Advance(notifier, TimeSpan.FromSeconds(40));
        Assert.Empty(_mail.Sent);

        await Advance(notifier, TimeSpan.FromSeconds(21));
        Assert.Single(_mail.Sent);
        Assert.Contains("ERROR", _mail.Sent[0].Subject);
    }

    [Fact]
    public async Task ContinuingError_GetsOneReminderPerDay()
    {
        var alerts = NewAlerts();
        var notifier = Notifier(alerts);
        alerts.Raise("k", AlertSeverity.Error, "t", "d");
        await Advance(notifier, TimeSpan.FromSeconds(61));

        await Advance(notifier, TimeSpan.FromHours(23));
        Assert.Single(_mail.Sent);
        await Advance(notifier, TimeSpan.FromHours(1));
        await Advance(notifier, TimeSpan.FromMinutes(1));
        Assert.Equal(2, _mail.Sent.Count);
        Assert.Contains("still active", _mail.Sent[1].Subject);
    }

    [Fact]
    public async Task NoAddress_SendsNothing_ButAlertIsActive_AndAddingOneLaterDoesNotBackfill()
    {
        _settings.Alerts.Email = "";
        var alerts = NewAlerts();
        var notifier = Notifier(alerts);
        alerts.Raise("k", AlertSeverity.Warning, "t", "d");
        await Advance(notifier, TimeSpan.FromSeconds(61));

        Assert.Empty(_mail.Sent);
        Assert.Single(alerts.Active);

        _settings.Alerts.Email = "ops@example.com";
        await Advance(notifier, TimeSpan.FromMinutes(5));
        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task SendFailure_RetriesWithBackoff_ThenSucceeds()
    {
        var alerts = NewAlerts();
        var notifier = Notifier(alerts);
        _mail.FailNext = 2;
        alerts.Raise("k", AlertSeverity.Warning, "t", "d");

        await Advance(notifier, TimeSpan.FromSeconds(61)); // attempt 1 fails
        await Advance(notifier, TimeSpan.FromSeconds(59));
        Assert.Equal(1, _mail.Attempts);
        await Advance(notifier, TimeSpan.FromSeconds(2)); // attempt 2 (1 min) fails
        await Advance(notifier, TimeSpan.FromMinutes(5)); // attempt 3 (5 min) succeeds

        Assert.Equal(3, _mail.Attempts);
        Assert.Single(_mail.Sent);
    }

    [Fact]
    public async Task StaleRetry_IsDropped_WhenStateMovesOn()
    {
        var alerts = NewAlerts();
        var notifier = Notifier(alerts);
        _mail.FailNext = 1;
        alerts.Raise("k", AlertSeverity.Warning, "t", "d");
        await Advance(notifier, TimeSpan.FromSeconds(61)); // Warning fails, retry queued

        alerts.Clear("k");
        await Advance(notifier, TimeSpan.FromSeconds(61)); // RESOLVED supersedes the queued Warning

        Assert.Single(_mail.Sent);
        Assert.Contains("RESOLVED", _mail.Sent[0].Subject);
    }

    [Fact]
    public async Task Restart_WithAlertStillActive_DoesNotReEmail()
    {
        var alerts = NewAlerts();
        alerts.Raise("k", AlertSeverity.Warning, "t", "d");
        await Advance(Notifier(alerts), TimeSpan.FromSeconds(61));
        Assert.Single(_mail.Sent);

        var restarted = NewAlerts();
        var notifier = Notifier(restarted);
        restarted.Raise("k", AlertSeverity.Warning, "t", "d");
        await Advance(notifier, TimeSpan.FromMinutes(10));

        Assert.Single(_mail.Sent);
        Assert.Single(restarted.Active);
    }

    [Fact]
    public async Task RestoredAlert_NobodyReasserts_ClearsAfterGrace()
    {
        var alerts = NewAlerts();
        alerts.Raise("k", AlertSeverity.Warning, "t", "d");
        await Advance(Notifier(alerts), TimeSpan.FromSeconds(61));

        var restarted = NewAlerts();
        var notifier = Notifier(restarted);
        await Advance(notifier, TimeSpan.FromMinutes(4));
        Assert.Single(restarted.Active);

        await Advance(notifier, TimeSpan.FromMinutes(2));
        await Advance(notifier, TimeSpan.FromSeconds(61));
        Assert.Empty(restarted.Active);
        Assert.Contains("RESOLVED", _mail.Sent[^1].Subject);
    }

    [Theory]
    [InlineData("ops@example.com", true)]
    [InlineData("first.last+tag@sub.example.co", true)]
    [InlineData("not-an-email", false)]
    [InlineData("a@b", false)]
    [InlineData("Ops <ops@example.com>", false)]
    [InlineData(" ops@example.com", false)]
    [InlineData("a@b.com,c@d.com", false)]
    public void EmailAddress_Validation(string value, bool valid) => Assert.Equal(valid, AlertMail.IsValidAddress(value));

    [Fact]
    public void Validate_EmptyAddressIsValid_InvalidOneIsRejected()
    {
        var s = new HostSettings();
        Assert.Empty(UiApp.Validate(s));

        s.Alerts.Email = "not-an-email";
        Assert.Contains(UiApp.Validate(s), e => e.Contains("valid email", StringComparison.Ordinal));

        s.Alerts.Email = "ops@example.com";
        Assert.Contains(UiApp.Validate(s), e => e.Contains("SMTP server", StringComparison.Ordinal));
        s.Alerts.SmtpHost = "smtp.example.com";
        s.Alerts.SmtpPort = 70000;
        Assert.Contains(UiApp.Validate(s), e => e.Contains("SMTP port", StringComparison.Ordinal));
    }

    [Fact]
    public void SmtpPassword_LivesOnlyInSecrets()
    {
        var store = new SettingsStore(Paths, 0, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var s = store.Effective;
        s.Alerts.Email = "ops@example.com";
        store.Save(s);
        var secrets = new SecretStore(Paths);
        secrets.SetSmtpPassword("hunter2-secret");

        Assert.DoesNotContain("hunter2-secret", File.ReadAllText(Paths.Settings), StringComparison.Ordinal);
        Assert.Equal("ops@example.com", new SettingsStore(Paths, 0, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Effective.Alerts.Email);
        Assert.True(secrets.HasSmtpPassword);

        secrets.SetSmtpPassword("");
        Assert.False(secrets.HasSmtpPassword);
    }

    [Fact]
    public void Scrub_RemovesPassword() =>
        Assert.Equal("535 auth failed for ********", AlertMail.Scrub("535 auth failed for hunter2", "hunter2"));

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeTransport : IAlertMailTransport
    {
        public List<AlertMailMessage> Sent { get; } = [];

        public int FailNext { get; set; }

        public int Attempts { get; private set; }

        public Task SendAsync(AlertMailMessage message, AlertSettings settings, string? password, CancellationToken ct)
        {
            Attempts++;
            if (FailNext > 0)
            {
                FailNext--;
                throw new IOException("connection refused");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
