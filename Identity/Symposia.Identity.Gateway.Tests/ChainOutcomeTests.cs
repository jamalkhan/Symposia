using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.ABI.FunctionEncoding;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Symposia.Identity.Domain;
using Symposia.Identity.Gateway.Chain;
using Symposia.Identity.Gateway.Endpoints;

namespace Symposia.Identity.Gateway.Tests;

/// <summary>#222: each way a relayed write can end is told apart, and a write that may still mine is never reported as failed.</summary>
public class ChainOutcomeTests
{
    private const string Signed = "0x02f86c0180843b9aca00";
    private static readonly string Hash = EthereumChainClient.HashOf(Signed);
    private static readonly WalletAddress Wallet = WalletAddress.TryParse("0x1111111111111111111111111111111111111111", out var w) ? w : throw new InvalidOperationException();

    private sealed class FakeTransport : IChainTransport
    {
        public Func<string>? Sign { get; set; }
        public Action? Send { get; set; }
        public Func<int, TransactionReceipt?> Receipt { get; set; } = _ => null;
        public Func<Transaction?> Tx { get; set; } = () => null;
        public int Sends { get; private set; }
        public int Resets { get; private set; }
        private int _polls;

        public Task<string> SignAsync<TFunction>(string contract, TFunction function) where TFunction : FunctionMessage, new() =>
            Task.FromResult(Sign is null ? Signed : Sign());

        public Task SendRawAsync(string signedTx, CancellationToken ct)
        {
            Sends++;
            Send?.Invoke();
            return Task.CompletedTask;
        }

        public Task<TransactionReceipt?> GetReceiptAsync(string txHash, CancellationToken ct) => Task.FromResult(Receipt(_polls++));

        public Task<Transaction?> GetTransactionAsync(string txHash, CancellationToken ct) => Task.FromResult(Tx());

        public Task ResetNonceAsync()
        {
            Resets++;
            return Task.CompletedTask;
        }
    }

    private static EthereumChainClient Client(FakeTransport t) => new(
        new GatewayOptions { ReceiptTimeout = TimeSpan.FromMilliseconds(200), ReceiptPollInterval = TimeSpan.FromMilliseconds(10), CapabilityRegistryAddress = "0xcap" },
        t, TimeProvider.System, NullLogger.Instance);

    private static Task<string> Grant(EthereumChainClient c) => c.GrantConsentAsync(Wallet, new byte[32], [Permission.EmailMarketing], new byte[32], new byte[32], 0, 0, new byte[65]);

    private static TransactionReceipt Receipt(int status, long gasUsed = 50_000) =>
        new() { Status = new HexBigInteger(status), GasUsed = new HexBigInteger(gasUsed), TransactionHash = Hash };

    private static Transaction Tx(long gas = 100_000) => new() { TransactionHash = Hash, Gas = new HexBigInteger(gas) };

    [Fact]
    public async Task Revert_BeforeBroadcast_IsRejected_WithTheOldWording()
    {
        var t = new FakeTransport { Sign = () => throw new SmartContractRevertException("ConsentRegistry: bad signature", "0x08c379a0") };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.Rejected, ex.Kind);
        Assert.StartsWith("GrantConsentFunction reverted:", ex.Message);
        Assert.Null(ex.TxHash);
        Assert.Equal(0, t.Sends);
    }

    [Fact]
    public async Task RpcDown_BeforeBroadcast_IsChainUnavailable_AndResetsTheNonce()
    {
        var t = new FakeTransport { Sign = () => throw new HttpRequestException("connection refused") };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.ChainUnavailable, ex.Kind);
        Assert.Equal(1, t.Resets);
    }

    [Fact]
    public async Task SendFails_ButTheNodeHasIt_IsPending_WithTheHash()
    {
        var t = new FakeTransport { Send = () => throw new RpcClientTimeoutException("timed out"), Tx = () => Tx() };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.Pending, ex.Kind);
        Assert.Equal(Hash, ex.TxHash);
        Assert.Equal(0, t.Resets);
    }

    [Fact]
    public async Task SendFails_AndTheNodeDoesntHaveIt_IsChainUnavailable_WithTheHash()
    {
        var t = new FakeTransport { Send = () => throw new RpcClientUnknownException("refused") };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.ChainUnavailable, ex.Kind);
        Assert.Equal(Hash, ex.TxHash);
        Assert.Equal(1, t.Resets);
    }

    [Fact]
    public async Task NoReceiptWithinTheTimeout_IsPending_NeverRejected()
    {
        var t = new FakeTransport();

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.Pending, ex.Kind);
        Assert.Equal(Hash, ex.TxHash);
        Assert.Equal(1, t.Sends);
    }

    [Fact]
    public async Task LateReceipt_AndLookupErrors_InsideTheTimeout_Succeed()
    {
        var t = new FakeTransport { Receipt = n => n switch { 0 => throw new HttpRequestException("blip"), < 3 => null, _ => Receipt(1) } };

        Assert.Equal(Hash, await Grant(Client(t)));
    }

    [Fact]
    public async Task ReceiptThatUsedTheWholeLimit_IsOutOfGas()
    {
        var t = new FakeTransport { Receipt = _ => Receipt(0, 100_000), Tx = () => Tx(100_000) };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.OutOfGas, ex.Kind);
        Assert.Contains("likely out of gas", ex.Message);
    }

    [Fact]
    public async Task FailedReceiptWithGasLeft_IsRejected_WithTheOldWording()
    {
        var t = new FakeTransport { Receipt = _ => Receipt(0, 40_000), Tx = () => Tx(100_000) };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.Rejected, ex.Kind);
        Assert.Equal("GrantConsentFunction reverted", ex.Message);
    }

    [Fact]
    public async Task Status_UnknownPendingConfirmedFailed()
    {
        var t = new FakeTransport();
        Assert.Null(await Client(t).GetTransactionStatusAsync(Hash));

        t.Tx = () => Tx();
        Assert.Equal("pending", (await Client(t).GetTransactionStatusAsync(Hash))!.Status);

        t.Receipt = _ => Receipt(1);
        Assert.Equal(new ChainTransactionStatus("confirmed"), await Client(t).GetTransactionStatusAsync(Hash));

        t.Receipt = _ => Receipt(0, 100_000);
        t.Tx = () => Tx(100_000);
        Assert.Equal(new ChainTransactionStatus("failed", "out_of_gas"), await Client(t).GetTransactionStatusAsync(Hash));
    }

    [Theory]
    [InlineData(3, null, ChainFailureKind.Rejected)]
    [InlineData(-32000, "execution reverted: no consent", ChainFailureKind.Rejected)]
    [InlineData(-32000, "nonce too low", ChainFailureKind.ChainUnavailable)]
    public void Classify_RpcErrors(int code, string? message, ChainFailureKind expected) =>
        Assert.Equal(expected, EthereumChainClient.ClassifyPreBroadcast(new RpcResponseException(new RpcError(code, message ?? "x"))));

    [Fact]
    public void Classify_UnknownException_IsNeverRejected() =>
        Assert.Equal(ChainFailureKind.ChainUnavailable, EthereumChainClient.ClassifyPreBroadcast(new InvalidOperationException("?")));

    [Theory]
    [InlineData(ChainFailureKind.Rejected, 422, null)]
    [InlineData(ChainFailureKind.OutOfGas, 502, null)]
    [InlineData(ChainFailureKind.ChainUnavailable, 503, "5")]
    [InlineData(ChainFailureKind.Pending, 202, null)]
    public async Task Results_MapEachKind(ChainFailureKind kind, int status, string? retryAfter)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();

        await ChainResults.ToResult(new ChainCallException("m", kind, Hash), http).ExecuteAsync(http);

        Assert.Equal(status, http.Response.StatusCode);
        Assert.Equal(retryAfter, http.Response.Headers.RetryAfter.FirstOrDefault());
        http.Response.Body.Position = 0;
        var body = new StreamReader(http.Response.Body).ReadToEnd();
        Assert.Contains(Hash, body);
        Assert.Contains($"\"{EthereumChainClient.Code(kind)}\"", body);
        if (kind == ChainFailureKind.Pending)
        {
            Assert.Equal($"/v1/transactions/{Hash}", http.Response.Headers.Location.ToString());
            Assert.DoesNotContain("error", body);
        }
    }
}
