using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using Nethereum.Web3;
using Symposia.Blockchain.Contracts;
using Wire = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire;

namespace Symposia.BlobStorage.Gateway.Rewards;

/// <summary>The chain reads the service-metrics reporter needs (#221); read-only, never signs or spends gas.</summary>
public interface IServiceMetricsChain
{
    Task<ulong> ChainIdAsync(CancellationToken ct);

    Task<ulong> CurrentEpochAsync(CancellationToken ct);

    /// <summary>The epoch's start, unix seconds.</summary>
    Task<long> EpochStartAsync(ulong epoch, CancellationToken ct);

    Task<bool> AcceptsReportsAsync(ulong epoch, CancellationToken ct);

    Task<bool> IsActiveAsync(string node, byte service, CancellationToken ct);

    /// <summary>Whether <c>metricOf(epoch, accountOf(node, service)).submitted</c>: a report already landed.</summary>
    Task<bool> SubmittedAsync(ulong epoch, string node, byte service, CancellationToken ct);
}

public sealed class NethereumServiceMetricsChain(ServiceMetricsOptions options) : IServiceMetricsChain
{
    /// <summary><c>INodeRegistry.NodeStatus.Active</c>.</summary>
    private const byte Active = 2;

    private readonly Web3 _web3 = new(options.RpcUrl);

    public async Task<ulong> ChainIdAsync(CancellationToken ct) => (ulong)(await _web3.Eth.ChainId.SendRequestAsync()).Value;

    public async Task<ulong> CurrentEpochAsync(CancellationToken ct) => (ulong)await _web3.Eth.GetContractQueryHandler<CurrentEpochFunction>()
        .QueryAsync<BigInteger>(options.EpochControllerAddress, new CurrentEpochFunction());

    public async Task<long> EpochStartAsync(ulong epoch, CancellationToken ct) => (long)await _web3.Eth.GetContractQueryHandler<EpochStartFunction>()
        .QueryAsync<BigInteger>(options.EpochControllerAddress, new EpochStartFunction { Epoch = epoch });

    public Task<bool> AcceptsReportsAsync(ulong epoch, CancellationToken ct) => _web3.Eth.GetContractQueryHandler<AcceptsReportsFunction>()
        .QueryAsync<bool>(options.EpochControllerAddress, new AcceptsReportsFunction { Epoch = epoch });

    public async Task<bool> IsActiveAsync(string node, byte service, CancellationToken ct) => await _web3.Eth.GetContractQueryHandler<ServiceStatusOfFunction>()
        .QueryAsync<byte>(options.StakingRegistryAddress, new ServiceStatusOfFunction { Node = node, Service = service }) == Active;

    public async Task<bool> SubmittedAsync(ulong epoch, string node, byte service, CancellationToken ct)
    {
        var account = await _web3.Eth.GetContractQueryHandler<AccountOfFunction>()
            .QueryAsync<string>(options.RewardDistributorAddress, new AccountOfFunction { Node = node, Service = service });
        var record = await _web3.Eth.GetContractQueryHandler<MetricOfFunction>()
            .QueryDeserializingToObjectAsync<MetricRecordOutput>(new MetricOfFunction { Epoch = epoch, Account = account }, options.RewardDistributorAddress);
        return record.Submitted;
    }
}

/// <summary>What the relay said: <see cref="Terminal"/> means stop trying this epoch.</summary>
public sealed record RelayOutcome(bool Submitted, bool Terminal, string? TransactionHash, string? Error);

public interface IServiceMetricsRelay
{
    Task<RelayOutcome> SubmitAsync(Wire.Request request, CancellationToken ct);
}

/// <summary>Posts reports to the Blockchain Gateway's relay, which pays the gas (#221).</summary>
public sealed class HttpServiceMetricsRelay(HttpClient http) : IServiceMetricsRelay
{
    public async Task<RelayOutcome> SubmitAsync(Wire.Request request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(Wire.Path, request, ct);
        var body = await ReadAsync(response, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => new RelayOutcome(true, false, body?.TransactionHash, null),
            // 409 is a terminal revert; any other 4xx bar 429 is a report the relay will never take.
            HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity =>
                new RelayOutcome(false, true, body?.TransactionHash, body?.Error ?? $"HTTP {(int)response.StatusCode}"),
            _ => new RelayOutcome(false, false, body?.TransactionHash, body?.Error ?? $"HTTP {(int)response.StatusCode}"),
        };
    }

    private static async Task<Wire.Response?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<Wire.Response>(ct);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
