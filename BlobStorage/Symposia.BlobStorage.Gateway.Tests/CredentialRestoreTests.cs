using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Domain.Credentials;
using Symposia.BlobStorage.Gateway.Auth;

namespace Symposia.BlobStorage.Gateway.Tests;

/// <summary>
/// A restore that reuses the epoch (#245 review: Lenny #1, Frank #3): the Authority's version drops below the
/// gateway's, it answers 410 AheadOfAuthority, and the gateway must install its snapshot rather than freeze.
/// </summary>
public sealed class CredentialRestoreTests
{
    private sealed class Authority(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://authority/") };
    }

    private static CredentialGrant Grant(string id, string key, CredentialStatus status) =>
        new(id, key, "t", "t-default", "app", AccessLevel.Read, ResourceScope.Tenant, null, [], false, status, DateTimeOffset.UtcNow, Secret: "s");

    private static (CredentialStore Cache, CredentialSync Sync) Gateway(Func<HttpRequestMessage, HttpResponseMessage> authority)
    {
        var options = Options.Create(new GatewayOptions());
        var cache = new CredentialStore(options);
        var meters = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        return (cache, new CredentialSync(cache, options, new Clients(new Authority(authority)), NullLogger<CredentialSync>.Instance, meters));
    }

    [Fact]
    public async Task AheadOfAuthority_InstallsTheRestoredSnapshot_EvenAtALowerVersionAndSameEpoch()
    {
        // The restored database predates the revocation the gateway already enforces.
        var restored = new AuthoritySnapshot(40, [new TenantState("t", TenantStatus.Active)], [Grant("c1", "AK1", CredentialStatus.Active)], []) { Epoch = "e1" };
        var (cache, sync) = Gateway(request => request.RequestUri!.AbsolutePath switch
        {
            "/internal/changes" => new HttpResponseMessage(HttpStatusCode.Gone) { Content = JsonContent.Create(new { error = "SnapshotRequired", reason = "AheadOfAuthority" }) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(restored, options: CredentialSync.Json) },
        });
        cache.Apply(new AuthoritySnapshot(50, [new TenantState("t", TenantStatus.Active)], [Grant("c1", "AK1", CredentialStatus.Revoked)], []) { Epoch = "e1" });

        Assert.True(await sync.SyncAsync(CancellationToken.None));

        Assert.Equal(40, cache.Version);
        Assert.Equal(1, sync.Counts["fallback_410"]);
    }

    [Fact]
    public async Task OrdinaryGone_DoesNotRollTheCacheBack()
    {
        var older = new AuthoritySnapshot(40, [], [], []) { Epoch = "e1" };
        var (cache, sync) = Gateway(request => request.RequestUri!.AbsolutePath switch
        {
            "/internal/changes" => new HttpResponseMessage(HttpStatusCode.Gone) { Content = JsonContent.Create(new { error = "SnapshotRequired", reason = "BehindRetention" }) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(older, options: CredentialSync.Json) },
        });
        cache.Apply(new AuthoritySnapshot(50, [], [], []) { Epoch = "e1" });

        Assert.False(await sync.SyncAsync(CancellationToken.None)); // a lagging replica's snapshot must not undo newer state
        Assert.Equal(50, cache.Version);
    }

    [Fact]
    public async Task PreDeltaAuthority_404_FallsBackToSnapshots_AndStopsAskingForDeltas() // AC10, CP-1
    {
        var changesCalls = 0;
        var (cache, sync) = Gateway(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/internal/changes")
            {
                changesCalls++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // A #3 Authority's snapshot: no epoch field at all.
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"version":7,"tenants":[],"credentials":[],"rules":[]}""", System.Text.Encoding.UTF8, "application/json") };
        });
        cache.Apply(new AuthoritySnapshot(5, [], [], []));

        await sync.SyncAsync(CancellationToken.None);
        await sync.SyncAsync(CancellationToken.None);

        Assert.Equal(1, changesCalls);
        Assert.Equal(1, sync.Counts["fallback_404"]);
        Assert.Equal(1, sync.Counts["snapshot"]); // the next poll goes straight to the snapshot
        Assert.Equal(7, cache.Version);
    }

    [Fact]
    public async Task PollingAlone_EnforcesARevocation_WithNoWatchStream() // AC4: the poll path, as when Watch is down
    {
        var revoked = new AuthorityDelta(50, 51, "e1", [], [Grant("c1", "AK1", CredentialStatus.Revoked) with { Secret = null }], [], []);
        var (cache, sync) = Gateway(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(revoked, options: CredentialSync.Json) });
        cache.Apply(new AuthoritySnapshot(50, [new TenantState("t", TenantStatus.Active)], [Grant("c1", "AK1", CredentialStatus.Active)], []) { Epoch = "e1" });

        Assert.True(await sync.SyncAsync(CancellationToken.None));

        Assert.Equal(CredentialStatus.Revoked, cache.Grant("AK1")!.Status);
        Assert.Equal(1, sync.Counts["delta"]);
    }
}
