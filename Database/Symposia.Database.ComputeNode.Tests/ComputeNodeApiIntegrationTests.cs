using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Symposia.Database.ComputeNode.Databases;
using Symposia.Database.ComputeNode.Supervision;
using Symposia.Database.ComputeNode.Tests.Supervision;

namespace Symposia.Database.ComputeNode.Tests;

/// <summary>Boots the full ComputeNode ASP.NET Core host in-process against a fake process launcher.</summary>
public sealed class ComputeNodeApiIntegrationTests : IClassFixture<ComputeNodeApiIntegrationTests.NodeFactory>
{
    private readonly NodeFactory _factory;
    private readonly HttpClient _client;

    public ComputeNodeApiIntegrationTests(NodeFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Healthz_ReportsHealthyAfterStartup()
    {
        var response = await _client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_ReportsReadyWhenUnderCapacity()
    {
        var response = await _client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostDatabases_PlacesDatabase_ThenGetListsIt()
    {
        var request = new PlaceDatabaseRequest("tenant-int-1", "cred", 16, [], []);

        var postResponse = await _client.PostAsJsonAsync("/databases", request);
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);

        var listResponse = await _client.GetFromJsonAsync<List<TenantDatabase>>("/databases");
        Assert.Contains(listResponse!, db => db.TenantDatabaseId == "tenant-int-1");
    }

    [Fact]
    public async Task DeleteDatabases_RemovesPlacedDatabase()
    {
        await _client.PostAsJsonAsync("/databases", new PlaceDatabaseRequest("tenant-int-2", "cred", 16, [], []));

        var deleteResponse = await _client.DeleteAsync("/databases/tenant-int-2");

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteDatabases_UnknownId_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/databases/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public sealed class NodeFactory : WebApplicationFactory<Program>
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"symposia-compute-test-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ComputeNode:DataRoot"] = _dataDir,
                    ["ComputeNode:NodeIdentityKeyPath"] = Path.Combine(_dataDir, "node-identity.pem"),
                    ["ComputeNode:MaxHostedDatabases"] = "8",
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProcessLauncher>();
                services.AddSingleton<IProcessLauncher, FakeProcessLauncher>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
    }
}
