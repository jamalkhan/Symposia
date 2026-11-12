namespace Symposia.Blockchain.Gateway.Tests;

public sealed class AnvilProcessTests
{
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
        using var http = new HttpClient();
        foreach (var anvil in new[] { a, b })
        {
            var rpc = await http.PostAsync(anvil.RpcUrl, new StringContent(
                """{"jsonrpc":"2.0","method":"eth_chainId","params":[],"id":1}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.Contains("\"0x7a69\"", await rpc.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_binary_that_exits_fails_fast_instead_of_hanging()
    {
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() => AnvilProcess.StartAsync("/usr/bin/false"));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
