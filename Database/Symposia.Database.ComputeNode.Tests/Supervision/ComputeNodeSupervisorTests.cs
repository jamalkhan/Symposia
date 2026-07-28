using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.Database.ComputeNode;
using Symposia.Database.ComputeNode.Databases;
using Symposia.Database.ComputeNode.Supervision;

namespace Symposia.Database.ComputeNode.Tests.Supervision;

public class ComputeNodeSupervisorTests
{
    private static ComputeNodeSupervisor CreateSupervisor(FakeProcessLauncher launcher, ComputeNodeOptions? options = null)
    {
        options ??= new ComputeNodeOptions
        {
            SupportedPostgresMajorVersion = 16,
            MaxHostedDatabases = 2,
            RestartBackoffBaseSeconds = 0,
        };

        return new ComputeNodeSupervisor(Options.Create(options), launcher, NullLoggerFactory.Instance);
    }

    private static PlaceDatabaseRequest Request(string id = "tenant-a", int pgVersion = 16) =>
        new(id, BlobBucketCredential: "narrow-scoped-credential", pgVersion, Extensions: [], SafekeeperPeers: []);

    [Fact]
    public void Start_BringsUpPageserverAndSafekeeper()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = CreateSupervisor(launcher);

        supervisor.Start();

        Assert.True(supervisor.IsLive);
        Assert.True(supervisor.IsHealthy);
        Assert.Equal(2, launcher.Launched.Count);
    }

    [Fact]
    public void PlaceDatabase_StartsPostgresProcessAndTracksDatabase()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = CreateSupervisor(launcher);
        supervisor.Start();

        var result = supervisor.PlaceDatabase(Request());

        Assert.Equal(PlaceDatabaseOutcome.Placed, result.Outcome);
        Assert.Single(supervisor.ListDatabases());
        Assert.Equal(3, launcher.Launched.Count); // pageserver + safekeeper + this postgres
    }

    [Fact]
    public void PlaceDatabase_NeverPassesCredentialAsCommandLineArgument()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = CreateSupervisor(launcher);
        supervisor.Start();

        supervisor.PlaceDatabase(Request());

        var postgres = launcher.Launched[^1];
        Assert.DoesNotContain("narrow-scoped-credential", postgres.Arguments);
        Assert.Equal("narrow-scoped-credential", postgres.Environment?["SYMPOSIA_BLOB_BUCKET_CREDENTIAL"]);
    }

    [Fact]
    public void PlaceDatabase_DuplicateTenantId_ReturnsConflict()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = CreateSupervisor(launcher);
        supervisor.Start();
        supervisor.PlaceDatabase(Request());

        var result = supervisor.PlaceDatabase(Request());

        Assert.Equal(PlaceDatabaseOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public void PlaceDatabase_UnsupportedVersion_Rejected()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = CreateSupervisor(launcher);
        supervisor.Start();

        var result = supervisor.PlaceDatabase(Request(pgVersion: 15));

        Assert.Equal(PlaceDatabaseOutcome.UnsupportedVersion, result.Outcome);
        Assert.Empty(supervisor.ListDatabases());
    }

    [Fact]
    public void PlaceDatabase_AtCapacity_Rejected()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = CreateSupervisor(launcher, new ComputeNodeOptions { SupportedPostgresMajorVersion = 16, MaxHostedDatabases = 1 });
        supervisor.Start();
        supervisor.PlaceDatabase(Request("tenant-a"));

        var result = supervisor.PlaceDatabase(Request("tenant-b"));

        Assert.Equal(PlaceDatabaseOutcome.CapacityExceeded, result.Outcome);
    }

    [Fact]
    public void CanAcceptNewPlacements_FalseWhenAtCapacity_TrueOtherwise()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = CreateSupervisor(launcher, new ComputeNodeOptions { SupportedPostgresMajorVersion = 16, MaxHostedDatabases = 1 });
        supervisor.Start();

        Assert.True(supervisor.CanAcceptNewPlacements);

        supervisor.PlaceDatabase(Request("tenant-a"));

        Assert.False(supervisor.CanAcceptNewPlacements);
    }

    [Fact]
    public void RemoveDatabase_StopsProcessAndForgetsDatabase()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = CreateSupervisor(launcher);
        supervisor.Start();
        supervisor.PlaceDatabase(Request());

        var removed = supervisor.RemoveDatabase("tenant-a");

        Assert.True(removed);
        Assert.Empty(supervisor.ListDatabases());
        Assert.True(launcher.Launched[^1].HasExited);
    }

    [Fact]
    public void RemoveDatabase_UnknownId_ReturnsFalse()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = CreateSupervisor(launcher);
        supervisor.Start();

        Assert.False(supervisor.RemoveDatabase("does-not-exist"));
    }
}
