using System.Net;
using System.Net.Sockets;

namespace Symposia.Blockchain.Gateway.Tests;

public sealed class AnvilProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("anvil-process-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("Listening on 127.0.0.1:56249", 56249)]
    [InlineData("Listening on 0.0.0.0:8545 ", 8545)]
    public void ParsePort_reads_anvils_listening_line(string line, int port) =>
        Assert.Equal(port, AnvilProcess.ParsePort(line));

    [Theory]
    [InlineData("eth_chainId")]
    [InlineData("Available Accounts")]
    [InlineData("Listening on 127.0.0.1:")]
    [InlineData("Not Listening on 127.0.0.1:1")]
    public void ParsePort_ignores_other_lines(string line) => Assert.Null(AnvilProcess.ParsePort(line));

    [Fact]
    public async Task Two_anvils_get_distinct_ports_and_each_answers_as_anvil()
    {
        await using var a = await AnvilProcess.StartAsync();
        await using var b = await AnvilProcess.StartAsync();

        Assert.NotEqual(a.Port, b.Port);
        await AssertIsAnvilAsync(a);
        await AssertIsAnvilAsync(b);
    }

    [Fact]
    public async Task A_listener_holding_a_pre_picked_port_cannot_capture_the_fixture()
    {
        // The old fixtures' failure: something else bound on the port picked for anvil (#186).
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;

        await using var anvil = await AnvilProcess.StartAsync();

        Assert.NotEqual(taken, anvil.Port);
        await AssertIsAnvilAsync(anvil);
    }

    [Fact]
    public async Task A_missing_binary_fails_with_a_clear_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AnvilProcess.StartAsync(Path.Combine(_dir, "no-such-anvil"), TimeSpan.FromSeconds(10)));
        Assert.Contains("no-such-anvil", ex.Message);
    }

    [Fact]
    public async Task An_anvil_that_exits_fails_fast_with_its_output()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AnvilProcess.StartAsync(Script("echo 'error: bad flag' >&2; exit 2"), TimeSpan.FromSeconds(30)))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("exited before listening", ex.Message);
        Assert.Contains("error: bad flag", ex.Message);
    }

    [Fact]
    public async Task An_anvil_that_never_reports_a_port_times_out_with_its_output()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AnvilProcess.StartAsync(Script("echo starting; exec sleep 30"), TimeSpan.FromSeconds(1)));
        Assert.Contains("did not report a port", ex.Message);
        Assert.Contains("starting", ex.Message);
    }

    private string Script(string body)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task AssertIsAnvilAsync(AnvilProcess anvil)
    {
        using var http = new HttpClient();
        var rpc = await http.PostAsync(anvil.RpcUrl, new StringContent(
            """{"jsonrpc":"2.0","method":"web3_clientVersion","params":[],"id":1}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Contains("\"anvil/", await rpc.Content.ReadAsStringAsync());
    }
}
