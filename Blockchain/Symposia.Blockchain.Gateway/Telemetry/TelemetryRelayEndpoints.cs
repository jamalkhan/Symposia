using System.Numerics;
using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;
using Symposia.Blockchain.Gateway.Chain;
using Symposia.Network.Node.Telemetry;
using Symposia.Network.Node.Telemetry.Committee;
using Wire = Symposia.Network.Node.Telemetry.Committee.TelemetryRelayWire;

namespace Symposia.Blockchain.Gateway.Telemetry;

/// <summary>
/// Relays committee checkpoints, node self-checkpoints and aggregated records to
/// <c>TelemetryCheckpointRegistry</c>, paying gas from the relayer (issue #158 FR5). The contract
/// stays the authority. The Gateway only rejects malformed or oversized payloads cheaply, stores
/// self-checkpoint batches so nodes don't need blob credentials, and relies on gas estimation to
/// simulate before anything is spent. "Already committed/written" answers 200, so racing
/// committee members don't see errors.
/// </summary>
public static class TelemetryRelayEndpoints
{
    private const int MaxSignatures = 64;
    private const int MaxBatchBytes = 4 << 20;

    private static readonly string[] AlreadyDone =
    [
        Selector("CheckpointAlreadyCommitted(uint256,uint256)"),
        Selector("ChunkAlreadyWritten(uint256,uint256)"),
    ];

    public static IEndpointRouteBuilder MapTelemetryRelay(this IEndpointRouteBuilder app)
    {
        app.MapPost(Wire.CheckpointsPath, async (Wire.CheckpointRequest req, RelayerAccount relayer, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            if (Registry(options) is not { } registry)
            {
                return Results.NotFound();
            }

            if (!IsBytes32(req.MerkleRoot) || !IsBytes32(req.BlobAddress) || !ValidSignatures(req.Signatures))
            {
                return Results.BadRequest("Malformed checkpoint.");
            }

            return await SendAsync(relayer, registry, new CommitCheckpointFunction
            {
                Epoch = req.Epoch,
                Index = req.Checkpoint,
                MerkleRoot = req.MerkleRoot.HexToByteArray(),
                BlobAddress = req.BlobAddress.HexToByteArray(),
                SampleCount = req.SampleCount,
                Signatures = [.. req.Signatures.Select(s => s.HexToByteArray())],
            }, ct);
        });

        app.MapPost(Wire.SelfCheckpointsPath, async (Wire.SelfCheckpointRequest req, RelayerAccount relayer, ITelemetryBlobStore blobs,
            IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            if (Registry(options) is not { } registry)
            {
                return Results.NotFound();
            }

            byte[] batch;
            try
            {
                batch = Convert.FromBase64String(req.Batch);
            }
            catch (FormatException)
            {
                return Results.BadRequest("Batch is not base64.");
            }

            if (!AddressUtil.Current.IsValidEthereumAddressHexFormat(req.Node) || !IsBytes32(req.MerkleRoot) || !IsBytes32(req.BlobAddress)
                || batch.Length is 0 or > MaxBatchBytes || !TelemetryBatchCodec.Address(batch).AsSpan().SequenceEqual(req.BlobAddress.HexToByteArray()))
            {
                return Results.BadRequest("Malformed self-checkpoint, or the batch doesn't hash to its blob address.");
            }

            var digest = new TelemetryEip712(await ChainIdAsync(relayer), registry)
                .SelfCheckpointDigest(req.Node, req.Epoch, req.Checkpoint, req.MerkleRoot.HexToByteArray(), req.BlobAddress.HexToByteArray(), req.SampleCount);
            if (!string.Equals(TelemetryEip712.Recover(digest, req.Signature), req.Node, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Signature doesn't match the node.");
            }

            var result = await SendAsync(relayer, registry, new SelfCheckpointForFunction
            {
                Node = req.Node,
                Epoch = req.Epoch,
                Index = req.Checkpoint,
                MerkleRoot = req.MerkleRoot.HexToByteArray(),
                BlobAddress = req.BlobAddress.HexToByteArray(),
                SampleCount = req.SampleCount,
                Signature = req.Signature.HexToByteArray(),
            }, ct);
            // Store only what the chain accepted, so the blob store can't be filled with junk by unsigned callers.
            if (result is IStatusCodeHttpResult { StatusCode: 200 })
            {
                await blobs.PutAsync(req.BlobAddress.HexToByteArray(), batch, ct);
            }

            return result;
        });

        app.MapPost(Wire.RecordsPath, async (Wire.RecordsRequest req, RelayerAccount relayer, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            if (Registry(options) is not { } registry)
            {
                return Results.NotFound();
            }

            if (req.ChunkCount <= 0 || req.ChunkIndex < 0 || req.ChunkIndex >= req.ChunkCount || req.Records.Count > 1000 || !ValidSignatures(req.Signatures))
            {
                return Results.BadRequest("Malformed records chunk.");
            }

            return await SendAsync(relayer, registry, new SubmitAggregatedRecordsFunction
            {
                Epoch = req.Epoch,
                ChunkIndex = req.ChunkIndex,
                ChunkCount = req.ChunkCount,
                Inputs = [.. req.Records.Select(r => r.ToInput())],
                Signatures = [.. req.Signatures.Select(s => s.HexToByteArray())],
            }, ct);
        });

        return app;
    }

    private static async Task<IResult> SendAsync<TFunction>(RelayerAccount relayer, string registry, TFunction function, CancellationToken ct)
        where TFunction : FunctionMessage, new()
    {
        try
        {
            var receipt = await relayer.SendAsync(web3 => web3.Eth.GetContractTransactionHandler<TFunction>()
                .SendRequestAndWaitForReceiptAsync(registry, function, ct), ct);
            return receipt.Status.Value == 1
                ? Results.Ok(new { transactionHash = receipt.TransactionHash })
                : Results.Problem($"Transaction {receipt.TransactionHash} reverted.", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex) when (AlreadyDone.Any(s => Describe(ex).Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Ok(new { already = true });
        }
        catch (Exception ex) when (ex is Nethereum.JsonRpc.Client.RpcResponseException or SmartContractCustomErrorRevertException)
        {
            return Results.UnprocessableEntity(Describe(ex));
        }
    }

    /// <summary>Message plus any revert data, so custom-error selectors can be matched.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        SmartContractCustomErrorRevertException c => $"{c.Message} {c.ExceptionEncodedData}",
        Nethereum.JsonRpc.Client.RpcResponseException r => $"{r.Message} {r.RpcError?.Data}",
        _ => ex.Message,
    };

    private static string? Registry(IOptions<GatewayOptions> options) =>
        string.IsNullOrEmpty(options.Value.TelemetryRegistryAddress) ? null : options.Value.TelemetryRegistryAddress;

    private static Task<ulong>? _chainId;

    private static Task<ulong> ChainIdAsync(RelayerAccount relayer) =>
        _chainId ??= relayer.Web3.Eth.ChainId.SendRequestAsync().ContinueWith(t => (ulong)t.Result.Value);

    private static bool IsBytes32(string hex) => hex.IsHex() && hex.HexToByteArray().Length == 32;

    private static bool ValidSignatures(IReadOnlyList<string> signatures) =>
        signatures.Count is > 0 and <= MaxSignatures && signatures.All(s => s.IsHex() && s.HexToByteArray().Length == 65);

    private static string Selector(string signature) =>
        Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes(signature))[..4].ToHex(false);

    [Function("commitCheckpoint")]
    private sealed class CommitCheckpointFunction : FunctionMessage
    {
        [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
        [Parameter("uint256", "checkpointIndex", 2)] public BigInteger Index { get; set; }
        [Parameter("bytes32", "merkleRoot", 3)] public byte[] MerkleRoot { get; set; } = [];
        [Parameter("bytes32", "blobAddress", 4)] public byte[] BlobAddress { get; set; } = [];
        [Parameter("uint32", "sampleCount", 5)] public uint SampleCount { get; set; }
        [Parameter("bytes[]", "signatures", 6)] public List<byte[]> Signatures { get; set; } = [];
    }

    [Function("selfCheckpointFor")]
    private sealed class SelfCheckpointForFunction : FunctionMessage
    {
        [Parameter("address", "node", 1)] public string Node { get; set; } = "";
        [Parameter("uint256", "epoch", 2)] public BigInteger Epoch { get; set; }
        [Parameter("uint256", "checkpointIndex", 3)] public BigInteger Index { get; set; }
        [Parameter("bytes32", "merkleRoot", 4)] public byte[] MerkleRoot { get; set; } = [];
        [Parameter("bytes32", "blobAddress", 5)] public byte[] BlobAddress { get; set; } = [];
        [Parameter("uint32", "sampleCount", 6)] public uint SampleCount { get; set; }
        [Parameter("bytes", "signature", 7)] public byte[] Signature { get; set; } = [];
    }

    [Function("submitAggregatedRecords")]
    private sealed class SubmitAggregatedRecordsFunction : FunctionMessage
    {
        [Parameter("uint256", "epoch", 1)] public BigInteger Epoch { get; set; }
        [Parameter("uint256", "chunkIndex", 2)] public BigInteger ChunkIndex { get; set; }
        [Parameter("uint256", "chunkCount", 3)] public BigInteger ChunkCount { get; set; }
        [Parameter("tuple[]", "inputs", 4, structTypeName: "AggregatedMetricInput[]")] public List<AggregatedMetricInput> Inputs { get; set; } = [];
        [Parameter("bytes[]", "signatures", 5)] public List<byte[]> Signatures { get; set; } = [];
    }
}
