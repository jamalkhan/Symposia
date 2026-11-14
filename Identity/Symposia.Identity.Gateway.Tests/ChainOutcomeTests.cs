using System.Net.Http;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethereum.ABI.FunctionEncoding;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Newtonsoft.Json.Linq;
using Symposia.Identity.Domain;
using Symposia.Identity.Gateway.Chain;
using Symposia.Identity.Gateway.Endpoints;

namespace Symposia.Identity.Gateway.Tests;

/// <summary>#222: each way a relayed write can end is told apart, and a write that may still mine is never reported as failed.</summary>
public class ChainOutcomeTests
{
    private const string Signed = "0x02f86c0180843b9aca00";
    private const string Capability = "0x00000000000000000000000000000000000000ca";
    private static readonly string Hash = EthereumChainClient.HashOf(Signed);
    private static readonly WalletAddress Wallet = WalletAddress.TryParse("0x1111111111111111111111111111111111111111", out var w) ? w : throw new InvalidOperationException();

    private sealed class FakeTransport : IChainTransport
    {
        public Func<CancellationToken, Task<string>>? Sign { get; set; }
        public Func<CancellationToken, Task>? Send { get; set; }
        public Func<int, CancellationToken, Task<TransactionReceipt?>> Receipt { get; set; } = (_, _) => Task.FromResult<TransactionReceipt?>(null);
        public Func<Transaction?> Tx { get; set; } = () => null;
        public int Sends { get; private set; }
        public int Resets { get; private set; }
        public int ReceiptCallsAfterCancel { get; private set; }
        public CancellationToken? CancelledBy { get; set; }
        private int _polls;

        public Task<string> SignAsync<TFunction>(string contract, TFunction function, CancellationToken ct) where TFunction : FunctionMessage, new() =>
            Sign is null ? Task.FromResult(Signed) : Sign(ct);

        public Task SendRawAsync(string signedTx, CancellationToken ct)
        {
            Sends++;
            return Send is null ? Task.CompletedTask : Send(ct);
        }

        public Task<TransactionReceipt?> GetReceiptAsync(string txHash, CancellationToken ct)
        {
            if (CancelledBy is { IsCancellationRequested: true })
            {
                ReceiptCallsAfterCancel++;
            }

            return Receipt(_polls++, ct);
        }

        public Task<Transaction?> GetTransactionAsync(string txHash, CancellationToken ct) => Task.FromResult(Tx());

        public Task ResetNonceAsync()
        {
            Resets++;
            return Task.CompletedTask;
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static EthereumChainClient Client(FakeTransport t, ILogger? logger = null, int receiptMs = 200, int stepMs = 200) => new(
        new GatewayOptions
        {
            ReceiptTimeout = TimeSpan.FromMilliseconds(receiptMs),
            ReceiptPollInterval = TimeSpan.FromMilliseconds(10),
            RpcStepTimeout = TimeSpan.FromMilliseconds(stepMs),
            GateTimeout = TimeSpan.FromMilliseconds(500),
            CapabilityRegistryAddress = Capability,
        },
        t, new Web3("http://127.0.0.1:1"), TimeProvider.System, logger ?? new ListLogger());

    private static Task<string> Grant(EthereumChainClient c, CancellationToken ct = default) =>
        c.GrantConsentAsync(Wallet, new byte[32], [Permission.EmailMarketing], new byte[32], new byte[32], 0, 0, new byte[65], ct);

    private static Task<ulong> Issue(EthereumChainClient c) => c.IssueCapabilityAsync(Wallet, new byte[32], Permission.EmailMarketing);

    private static Func<int, CancellationToken, Task<TransactionReceipt?>> Returns(TransactionReceipt? r) => (_, _) => Task.FromResult(r);

    private static TransactionReceipt Receipt(int status, long gasUsed = 50_000, string? to = null, JArray? logs = null) =>
        new() { Status = new HexBigInteger(status), GasUsed = new HexBigInteger(gasUsed), TransactionHash = Hash, To = to, Logs = logs ?? [] };

    private static Transaction Tx(long gas = 100_000) => new() { TransactionHash = Hash, Gas = new HexBigInteger(gas) };

    private static JArray IssuedLog(ulong tokenId)
    {
        static string Word(string hex) => "0x" + hex.PadLeft(64, '0');
        var signature = "0x" + Nethereum.Util.Sha3Keccack.Current.CalculateHash("CapabilityIssued(uint256,address,bytes32,uint8,uint64,uint64)");
        return JArray.FromObject(new[]
        {
            new
            {
                address = Capability,
                topics = new[] { signature, Word(tokenId.ToString("x")), Word(Wallet.Value[2..]), Word("") },
                data = Word("1") + new string('0', 63) + "5" + new string('0', 63) + "4",
                logIndex = "0x0",
            },
        });
    }

    [Fact]
    public async Task Revert_BeforeBroadcast_IsRejected_WithTheOldWording()
    {
        var t = new FakeTransport { Sign = _ => throw new SmartContractRevertException("ConsentRegistry: bad signature", "0x08c379a0") };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.Rejected, ex.Kind);
        Assert.StartsWith("GrantConsentFunction reverted: ", ex.Message);
        Assert.Contains("ConsentRegistry: bad signature", ex.Message);
        Assert.Null(ex.TxHash);
        Assert.Equal(0, t.Sends);
    }

    [Fact]
    public async Task CapabilityRevert_BeforeBroadcast_KeepsItsExactOldWording()
    {
        var t = new FakeTransport { Sign = _ => throw new SmartContractRevertException("CapabilityRegistry: no active consent", "0x08c379a0") };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Issue(Client(t)));

        Assert.Equal(ChainFailureKind.Rejected, ex.Kind);
        Assert.StartsWith("CapabilityRegistry.issueCapability reverted: ", ex.Message);
        Assert.Contains("CapabilityRegistry: no active consent", ex.Message);
    }

    [Fact]
    public async Task RpcDown_BeforeBroadcast_IsChainUnavailable_AndResetsTheNonce()
    {
        var t = new FakeTransport { Sign = _ => throw new HttpRequestException("connection refused") };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.ChainUnavailable, ex.Kind);
        Assert.Equal(1, t.Resets);
        Assert.Equal(0, t.Sends);
    }

    [Fact]
    public async Task HungSigning_IsBoundedByTheStepTimeout_AsChainUnavailable()
    {
        var t = new FakeTransport { Sign = ct => Task.Delay(Timeout.Infinite, ct).ContinueWith<string>(_ => throw new OperationCanceledException(ct)) };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t, stepMs: 50)));

        Assert.Equal(ChainFailureKind.ChainUnavailable, ex.Kind);
        Assert.Equal(0, t.Sends);
    }

    [Fact]
    public async Task ConnectionNeverMade_IsChainUnavailable_WithTheHash_AndResetsTheNonce()
    {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused));
        var t = new FakeTransport { Send = _ => throw new RpcClientUnknownException("send failed", refused) };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.ChainUnavailable, ex.Kind);
        Assert.Equal(Hash, ex.TxHash);
        Assert.Equal(1, t.Resets);
    }

    [Fact]
    public async Task NodeRefusesTheSend_IsChainUnavailable()
    {
        var t = new FakeTransport { Send = _ => throw new RpcResponseException(new RpcError(-32000, "insufficient funds for gas")) };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.ChainUnavailable, ex.Kind);
        Assert.Equal(1, t.Resets);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("already known")]
    [InlineData("reset")]
    public async Task AmbiguousSend_IsWaitedFor_NeverReportedAsSafeToRetry(string failure)
    {
        Exception error = failure switch
        {
            "timeout" => new RpcClientTimeoutException("timed out"),
            "already known" => new RpcResponseException(new RpcError(-32000, "already known")),
            _ => new RpcClientUnknownException("reset", new HttpRequestException(HttpRequestError.ResponseEnded, "ended")),
        };
        var t = new FakeTransport { Send = _ => throw error, Receipt = (n, _) => Task.FromResult(n < 2 ? null : Receipt(1)) };

        Assert.Equal(Hash, await Grant(Client(t)));
        Assert.Equal(0, t.Resets);
        Assert.Equal(1, t.Sends);
    }

    [Fact]
    public async Task HungSend_IsBoundedByTheStepTimeout_ThenWaitedFor()
    {
        var t = new FakeTransport { Send = ct => Task.Delay(Timeout.Infinite, ct), Tx = () => Tx() };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t, stepMs: 50)));

        Assert.Equal(ChainFailureKind.Pending, ex.Kind);
        Assert.Equal(Hash, ex.TxHash);
        Assert.Equal(0, t.Resets);
    }

    [Fact]
    public async Task NoReceiptWithinTheTimeout_IsPending_AndLogsTheHash()
    {
        var logger = new ListLogger();
        var t = new FakeTransport { Tx = () => Tx() };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t, logger)));

        Assert.Equal(ChainFailureKind.Pending, ex.Kind);
        Assert.Equal(Hash, ex.TxHash);
        Assert.Equal(1, t.Sends);
        Assert.Equal(0, t.Resets);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(Hash) && e.Message.Contains("pending"));
    }

    [Fact]
    public async Task DroppedTransaction_ReReadsTheNonce_SoLaterWritesArentWedged()
    {
        var t = new FakeTransport { Tx = () => null };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.Pending, ex.Kind);
        Assert.Equal(1, t.Resets);
    }

    [Fact]
    public async Task HungReceiptPoll_IsBoundedByTheReceiptTimeout()
    {
        var t = new FakeTransport { Receipt = (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<TransactionReceipt?>(_ => throw new OperationCanceledException(ct)), Tx = () => Tx() };
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t, receiptMs: 100)));

        Assert.Equal(ChainFailureKind.Pending, ex.Kind);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ClientGoesAway_DuringTheWait_IsPending_WithTheHash_AndPollingStops()
    {
        using var cts = new CancellationTokenSource();
        var logger = new ListLogger();
        var t = new FakeTransport { CancelledBy = cts.Token };
        t.Receipt = (n, _) =>
        {
            if (n == 1)
            {
                cts.Cancel();
            }

            return Task.FromResult<TransactionReceipt?>(null);
        };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t, logger, receiptMs: 5_000), cts.Token));

        Assert.Equal(ChainFailureKind.Pending, ex.Kind);
        Assert.Equal(Hash, ex.TxHash);
        Assert.Equal(0, t.ReceiptCallsAfterCancel);
        Assert.Equal(0, t.Resets);
        Assert.Contains(logger.Entries, e => e.Message.Contains(Hash));
    }

    [Fact]
    public async Task ClientGoesAway_DuringTheSend_DoesntCancelTheBroadcast()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken sendToken = default;
        var t = new FakeTransport
        {
            Send = ct =>
            {
                sendToken = ct;
                cts.Cancel();
                return Task.CompletedTask;
            },
            Tx = () => Tx(),
        };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t), cts.Token));

        Assert.False(sendToken.IsCancellationRequested);
        Assert.Equal(ChainFailureKind.Pending, ex.Kind);
    }

    [Fact]
    public async Task LateReceipt_AndLookupErrors_InsideTheTimeout_Succeed()
    {
        var t = new FakeTransport { Receipt = (n, _) => n switch { 0 => throw new HttpRequestException("blip"), < 3 => Task.FromResult<TransactionReceipt?>(null), _ => Task.FromResult<TransactionReceipt?>(Receipt(1)) } };

        Assert.Equal(Hash, await Grant(Client(t)));
        Assert.Equal(1, t.Sends);
    }

    [Fact]
    public async Task ReceiptThatUsedTheWholeLimit_IsOutOfGas()
    {
        var t = new FakeTransport { Receipt = Returns(Receipt(0, 100_000)), Tx = () => Tx(100_000) };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.OutOfGas, ex.Kind);
        Assert.Contains("likely out of gas", ex.Message);
    }

    [Fact]
    public async Task FailedReceiptWithGasLeft_IsRejected_WithTheOldWording()
    {
        var t = new FakeTransport { Receipt = Returns(Receipt(0, 40_000)), Tx = () => Tx(100_000) };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Grant(Client(t)));

        Assert.Equal(ChainFailureKind.Rejected, ex.Kind);
        Assert.Equal("GrantConsentFunction reverted", ex.Message);
    }

    [Fact]
    public async Task Capability_Success_DecodesTheTokenId()
    {
        var t = new FakeTransport { Receipt = Returns(Receipt(1, to: Capability, logs: IssuedLog(42))) };

        Assert.Equal(42UL, await Issue(Client(t)));
    }

    [Fact]
    public async Task Capability_SuccessWithoutItsEvent_IsAServerFault_NotARejection()
    {
        var t = new FakeTransport { Receipt = Returns(Receipt(1, to: Capability)) };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Issue(Client(t)));

        Assert.Equal(ChainFailureKind.Fault, ex.Kind);
        Assert.Equal(Hash, ex.TxHash);
    }

    [Fact]
    public async Task Capability_Pending_CarriesTheHash()
    {
        var t = new FakeTransport { Tx = () => Tx() };

        var ex = await Assert.ThrowsAsync<ChainCallException>(() => Issue(Client(t)));

        Assert.Equal(ChainFailureKind.Pending, ex.Kind);
        Assert.Equal(Hash, ex.TxHash);
        Assert.Equal(1, t.Sends);
    }

    [Fact]
    public async Task Status_UnknownPendingConfirmedFailed()
    {
        var t = new FakeTransport();
        Assert.Null(await Client(t).GetTransactionStatusAsync(Hash));

        t.Tx = () => Tx();
        Assert.Equal(ChainTxState.Pending, (await Client(t).GetTransactionStatusAsync(Hash))!.State);

        t.Receipt = Returns(Receipt(1));
        Assert.Equal(new ChainTransactionStatus(ChainTxState.Confirmed), await Client(t).GetTransactionStatusAsync(Hash));

        t.Receipt = Returns(Receipt(0, 100_000));
        t.Tx = () => Tx(100_000);
        Assert.Equal(new ChainTransactionStatus(ChainTxState.Failed, ChainFailureKind.OutOfGas), await Client(t).GetTransactionStatusAsync(Hash));
    }

    [Fact]
    public async Task Status_TokenId_OnlyForTheCapabilityRegistry()
    {
        var t = new FakeTransport { Receipt = Returns(Receipt(1, to: Capability, logs: IssuedLog(7))) };
        Assert.Equal(7UL, (await Client(t).GetTransactionStatusAsync(Hash))!.TokenId);

        t.Receipt = Returns(Receipt(1, to: "0x00000000000000000000000000000000000000c0", logs: IssuedLog(7)));
        Assert.Null((await Client(t).GetTransactionStatusAsync(Hash))!.TokenId);
    }

    [Theory]
    [InlineData(3, "x", ChainFailureKind.Rejected)]
    [InlineData(-32000, "execution reverted: no consent", ChainFailureKind.Rejected)]
    [InlineData(-32000, "nonce too low", ChainFailureKind.ChainUnavailable)]
    [InlineData(-32005, "rate limited; revert later", ChainFailureKind.ChainUnavailable)]
    public void Classify_RpcErrors(int code, string message, ChainFailureKind expected) =>
        Assert.Equal(expected, EthereumChainClient.ClassifyPreBroadcast(new RpcResponseException(new RpcError(code, message))));

    [Fact]
    public void Classify_RevertDataIsHex_ProviderObjectDataIsNot()
    {
        Assert.Equal(ChainFailureKind.Rejected,
            EthereumChainClient.ClassifyPreBroadcast(new RpcResponseException(new RpcError(-32603, "x", new JValue("0x08c379a0")))));
        Assert.Equal(ChainFailureKind.ChainUnavailable,
            EthereumChainClient.ClassifyPreBroadcast(new RpcResponseException(new RpcError(429, "x", JObject.FromObject(new { see = "https://provider/limits" })))));
    }

    [Fact]
    public void Classify_UnknownException_IsNeverRejected() =>
        Assert.Equal(ChainFailureKind.ChainUnavailable, EthereumChainClient.ClassifyPreBroadcast(new InvalidOperationException("?")));

    [Fact]
    public void WireNames_CoverEveryKind()
    {
        foreach (var kind in Enum.GetValues<ChainFailureKind>())
        {
            Assert.False(string.IsNullOrEmpty(kind.ToWireName()));
        }
    }

    [Theory]
    [InlineData("0x" + "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34", true)]
    [InlineData("ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34", false)]
    [InlineData("0X" + "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34", false)]
    [InlineData("0x" + "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd3", false)]
    [InlineData("0x" + "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd345", false)]
    [InlineData("0x" + "zz12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34", false)]
    public void TxHash_Validation(string value, bool valid) => Assert.Equal(valid, TransactionEndpoints.IsTxHash(value));

    [Theory]
    [InlineData(ChainFailureKind.Rejected, 422, null)]
    [InlineData(ChainFailureKind.OutOfGas, 502, null)]
    [InlineData(ChainFailureKind.Fault, 502, null)]
    [InlineData(ChainFailureKind.ChainUnavailable, 503, "5")]
    public async Task Results_FailureBodies_AreExactlyErrorCodeTxHash(ChainFailureKind kind, int status, string? retryAfter)
    {
        var (http, body) = await Execute(new ChainCallException(kind, "m", Hash));

        Assert.Equal(status, http.Response.StatusCode);
        Assert.Equal(retryAfter, http.Response.Headers.RetryAfter.FirstOrDefault());
        Assert.Equal(new[] { "code", "error", "txHash" }, body.Properties().Select(p => p.Name).Order());
        Assert.Equal(kind.ToWireName(), (string?)body["code"]);
        Assert.Equal(Hash, (string?)body["txHash"]);
    }

    [Fact]
    public async Task Results_Pending_Is202_WithLocation_AndNoError()
    {
        var (http, body) = await Execute(new ChainCallException(ChainFailureKind.Pending, "m", Hash));

        Assert.Equal(202, http.Response.StatusCode);
        Assert.Equal($"/v1/transactions/{Hash}", http.Response.Headers.Location.ToString());
        Assert.Equal(new[] { "status", "txHash" }, body.Properties().Select(p => p.Name).Order());
        Assert.Equal("pending", (string?)body["status"]);
    }

    private static async Task<(HttpContext Http, JObject Body)> Execute(ChainCallException ex)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await ChainResults.ToResult(ex, http).ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http, JObject.Parse(await new StreamReader(http.Response.Body).ReadToEndAsync()));
    }
}
