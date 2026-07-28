using Symposia.Database.ComputeNode.Proxy;

namespace Symposia.Database.ComputeNode.Tests.Proxy;

/// <summary>
/// Traces to #93's Gherkin scenarios and QA plan sections 2.3 (Authentication), 2.4 (Routing &
/// Failover), 2.5 (Read/Write Split), 2.6 (Idle & Max Connections), 2.7 (Wake-on-Connect).
/// </summary>
public sealed class ConnectionRouterTests
{
    private static ComputeEndpoint Endpoint(string nodeId) => new(nodeId, $"10.0.0.{nodeId.Length}", 5432);

    private sealed class FakeLifecycleClient(ComputeEndpoint resumedEndpoint) : ILifecycleClient
    {
        public int ResumeCallCount;

        public async Task<ComputeEndpoint> ResumeAsync(string databaseId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ResumeCallCount);
            await Task.Delay(10, cancellationToken);
            return resumedEndpoint;
        }
    }

    private static (ConnectionRouter Router, RoutingTableService Routing, AuthCacheService Auth, ConnectionAdmissionService Admission, FakeLifecycleClient Lifecycle) BuildRouter()
    {
        var routing = new RoutingTableService();
        var auth = new AuthCacheService();
        var admission = new ConnectionAdmissionService();
        var lifecycle = new FakeLifecycleClient(Endpoint("resumed-node"));
        var wake = new WakeOnConnectCoordinator(lifecycle, routing);
        var router = new ConnectionRouter(auth, admission, routing, wake);
        return (router, routing, auth, admission, lifecycle);
    }

    [Fact]
    public async Task RouteAsync_ValidCredential_RoutesToPrimary()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"));
        auth.Upsert("db-1", "tenant", "secret-hash");

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        Assert.Equal(ConnectionOutcome.Routed, decision.Outcome);
        Assert.Equal("primary-1", decision.Endpoint!.NodeId);
    }

    [Fact]
    public async Task RouteAsync_InvalidCredential_RejectedBeforeRoutingLookup()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"));
        auth.Upsert("db-1", "tenant", "secret-hash");

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "wrong-hash", ReadOnly: false));

        Assert.Equal(ConnectionOutcome.AuthRejected, decision.Outcome);
        Assert.Null(decision.Endpoint);
    }

    [Fact]
    public async Task RouteAsync_RevokedCredential_Rejected()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"));
        auth.Upsert("db-1", "tenant", "secret-hash");
        auth.Revoke("db-1", "tenant");

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        Assert.Equal(ConnectionOutcome.AuthRejected, decision.Outcome);
    }

    [Fact]
    public async Task RouteAsync_CredentialScopedPerDatabase_RejectedAgainstOtherDatabase()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"));
        routing.Upsert("db-2", Endpoint("primary-2"));
        auth.Upsert("db-2", "tenant", "secret-hash");

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        Assert.Equal(ConnectionOutcome.AuthRejected, decision.Outcome);
    }

    [Fact]
    public async Task RouteAsync_MaxConnectionCeiling_201stRejected()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"), maxConnections: 200);
        auth.Upsert("db-1", "tenant", "secret-hash");

        for (var i = 0; i < 200; i++)
            Assert.Equal(ConnectionOutcome.Routed, (await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false))).Outcome);

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        Assert.Equal(ConnectionOutcome.ConnectionLimitExceeded, decision.Outcome);
    }

    [Fact]
    public async Task RouteAsync_ReleaseThenReconnect_IsAdmittedAgain()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"), maxConnections: 1);
        auth.Upsert("db-1", "tenant", "secret-hash");
        await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        router.ReleaseConnection("db-1");
        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        Assert.Equal(ConnectionOutcome.Routed, decision.Outcome);
    }

    [Fact]
    public async Task RouteAsync_ReadOnlyWithReplica_RoutesToReplicaNotPrimary()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"), [Endpoint("replica-1")]);
        auth.Upsert("db-1", "tenant", "secret-hash");

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: true));

        Assert.Equal("replica-1", decision.Endpoint!.NodeId);
    }

    [Fact]
    public async Task RouteAsync_ReadOnlyWithNoReplica_FallsBackToPrimary()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"));
        auth.Upsert("db-1", "tenant", "secret-hash");

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: true));

        Assert.Equal("primary-1", decision.Endpoint!.NodeId);
    }

    [Fact]
    public async Task RouteAsync_WriteConnectionAlwaysRoutesToPrimaryEvenWithReplica()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"), [Endpoint("replica-1")]);
        auth.Upsert("db-1", "tenant", "secret-hash");

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        Assert.Equal("primary-1", decision.Endpoint!.NodeId);
    }

    [Fact]
    public async Task RouteAsync_MigrationCutover_NewConnectionsFollowUpdatedPrimary()
    {
        var (router, routing, auth, _, _) = BuildRouter();
        routing.Upsert("db-1", Endpoint("old-primary"));
        auth.Upsert("db-1", "tenant", "secret-hash");

        routing.UpdatePrimary("db-1", Endpoint("new-primary"));
        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        Assert.Equal("new-primary", decision.Endpoint!.NodeId);
    }

    [Fact]
    public async Task RouteAsync_SuspendedDatabase_TriggersWakeAndRoutesOnceResumed()
    {
        var (router, routing, auth, _, lifecycle) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"));
        routing.MarkStatus("db-1", RoutingStatus.Suspended);
        auth.Upsert("db-1", "tenant", "secret-hash");

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        Assert.Equal(ConnectionOutcome.Routed, decision.Outcome);
        Assert.Equal("resumed-node", decision.Endpoint!.NodeId);
        Assert.Equal(1, lifecycle.ResumeCallCount);
    }

    [Fact]
    public async Task RouteAsync_ConcurrentWakeRequests_SingleResumeCall()
    {
        var (router, routing, auth, _, lifecycle) = BuildRouter();
        routing.Upsert("db-1", Endpoint("primary-1"), maxConnections: 1000);
        routing.MarkStatus("db-1", RoutingStatus.Suspended);
        auth.Upsert("db-1", "tenant", "secret-hash");

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false)));
        var decisions = await Task.WhenAll(tasks);

        Assert.All(decisions, d => Assert.Equal(ConnectionOutcome.Routed, d.Outcome));
        Assert.Equal(1, lifecycle.ResumeCallCount);
    }

    [Fact]
    public async Task RouteAsync_NoRoutingEntry_ReturnsNoRoute()
    {
        var (router, _, auth, _, _) = BuildRouter();
        auth.Upsert("db-1", "tenant", "secret-hash");

        var decision = await router.RouteAsync(new ConnectionRequest("db-1", "tenant", "secret-hash", ReadOnly: false));

        Assert.Equal(ConnectionOutcome.NoRouteAvailable, decision.Outcome);
    }
}
