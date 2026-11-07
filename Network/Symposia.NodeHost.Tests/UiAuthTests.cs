using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Symposia.NodeHost.Components;
using Symposia.NodeHost.Diagnostics;
using Symposia.NodeHost.Settings;
using Symposia.NodeHost.Ui;

namespace Symposia.NodeHost.Tests;

/// <summary>Config UI authentication and secret handling over real HTTP (issue #133 FR-E2; QA TC-1, TC-2, TC-13).</summary>
public sealed class UiAuthTests : IAsyncLifetime
{
    private const string SetupToken = "abc123";
    private readonly TempDir _dir = new();
    private WebApplication _app = null!;
    private SecretStore _secrets = null!;
    private string _baseUrl = "";

    public async Task InitializeAsync()
    {
        var paths = new HostPaths(_dir.Path);
        var settings = new SettingsStore(paths, 0, new ConfigurationBuilder().Build());
        _secrets = new SecretStore(paths);
        var supervisor = new ComponentSupervisor(new HostContext(paths, _secrets, new LogBuffer()), settings);
        var port = FreePort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _app = UiApp.Create(port, supervisor, settings, _secrets, "0x5FbDB2315678afecb367f032d93F642f64180aa3", SetupToken);
        await _app.StartAsync();
    }

    private HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(_baseUrl) };

    [Fact]
    public async Task FirstRun_RequiresSetup_AndBlocksApi()
    {
        var client = Client();

        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.True(session.GetProperty("setupRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    // Issue #181 FR4: only an admin, only on a Local dev network, and nothing is written otherwise.
    [Fact]
    public async Task ResetRewards_NeedsSignIn_AndALocalDevNetwork()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/chain/reset-rewards", null)).StatusCode);

        await client.PostAsJsonAsync("/api/setup", new { password = "correct horse", setupToken = SetupToken });
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/chain/reset-rewards", null)).StatusCode);
        Assert.False(File.Exists(new HostPaths(_dir.Path).ResetRewardsFlag));
    }

    [Fact]
    public async Task NodeServices_ListsTheEarningsBreakdown_AfterSignIn()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/node-services")).StatusCode);

        await client.PostAsJsonAsync("/api/setup", new { password = "correct horse", setupToken = SetupToken });
        var list = await client.GetFromJsonAsync<JsonElement>("/api/node-services");

        Assert.Equal(11, list.GetArrayLength());
        var storage = list.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "storage");
        Assert.True(storage.GetProperty("earns").GetBoolean());
        Assert.Equal(7, storage.GetProperty("factors").GetArrayLength());
        var email = list.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "email-ip");
        Assert.False(email.GetProperty("earns").GetBoolean());
    }

    [Fact]
    public async Task Setup_SignsIn_AndCannotBeRepeated()
    {
        var client = Client();

        var setup = await client.PostAsJsonAsync("/api/setup", new { password = "correct horse" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Contains(setup.Headers.GetValues("Set-Cookie"), c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);

        var again = await Client().PostAsJsonAsync("/api/setup", new { password = "attacker pass" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.True(_secrets.VerifyPassword("correct horse"));
    }

    // Review (Lenny 1): two racing setups can't both win.
    [Fact]
    public void InitialPassword_CanOnlyBeSetOnce()
    {
        Assert.True(_secrets.TrySetInitialPassword("first password"));
        Assert.False(_secrets.TrySetInitialPassword("second password"));
        Assert.True(_secrets.VerifyPassword("first password"));
    }

    [Fact]
    public async Task Session_FromLoopback_DoesNotNeedSetupToken()
    {
        var session = await Client().GetFromJsonAsync<JsonElement>("/api/session");

        Assert.False(session.GetProperty("setupTokenRequired").GetBoolean());
    }

    [Fact]
    public async Task Setup_RejectsShortPassword()
    {
        var resp = await Client().PostAsJsonAsync("/api/setup", new { password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPasswordRejected_RightPasswordAccepted()
    {
        _secrets.SetPassword("correct horse");
        var client = Client();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/login", new { password = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/login", new { password = "correct horse" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task Login_IsRateLimited()
    {
        _secrets.SetPassword("correct horse");
        var client = Client();

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/login", new { password = "nope" });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/login", new { password = "correct horse" })).StatusCode);
    }

    // TC-13: the relayer key is stored but never returned.
    [Fact]
    public async Task RelayerKey_IsNeverReturned()
    {
        var client = Client();
        await client.PostAsJsonAsync("/api/setup", new { password = "correct horse" });
        var current = await client.GetFromJsonAsync<JsonElement>("/api/settings");

        var put = await client.PutAsJsonAsync("/api/settings", new { settings = current.GetProperty("settings"), relayerKey = "0xdeadbeefsecret" });
        put.EnsureSuccessStatusCode();
        var body = await client.GetStringAsync("/api/settings");

        Assert.DoesNotContain("deadbeef", body);
        Assert.Contains("\"relayerKeySet\":true", body);
        Assert.Equal("0xdeadbeefsecret", _secrets.RelayerPrivateKey);
    }

    [Fact]
    public async Task Logout_EndsSession()
    {
        var client = Client();
        await client.PostAsJsonAsync("/api/setup", new { password = "correct horse" });

        await client.PostAsync("/api/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _dir.Dispose();
    }
}
