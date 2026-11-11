using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Numerics;

namespace Symposia.Network.Node.Staking;

/// <summary>What a funding source answered.</summary>
public enum FundingResponse
{
    Ok,

    /// <summary>The network offers no such funding (not LocalDev, or an older host): stop asking.</summary>
    Unavailable,

    /// <summary>Refused for this service as it stands (e.g. suspended or banned): stop asking.</summary>
    Conflict,

    /// <summary>Not now (throttled, gateway busy): ask again later.</summary>
    Retry,
}

/// <summary>
/// Where a node's stake and its verification come from (#200). LocalDev uses the LocalDev gateway's
/// dev endpoints (<see cref="DevGatewayStakeFunding"/>); a testnet or mainnet operator flow can plug
/// in its own source without changing <see cref="NodeServiceStaker"/>.
/// </summary>
public interface IStakeFunding
{
    /// <summary>Makes sure the node holds enough SYM and gas to stake <paramref name="services"/>.</summary>
    Task<FundingResponse> FundAsync(IReadOnlyList<(byte Service, BigInteger Capacity)> services, CancellationToken ct);

    /// <summary>Gets a PendingVerification service verified.</summary>
    Task<FundingResponse> VerifyAsync(byte service, CancellationToken ct);
}

/// <summary>The LocalDev gateway's <c>/v1/dev/fund</c> and <c>/v1/dev/verify</c>, signed with the node key.</summary>
public sealed class DevGatewayStakeFunding(NodeIdentity identity, ulong chainId, Uri gateway, HttpClient http) : IStakeFunding
{
    public async Task<FundingResponse> FundAsync(IReadOnlyList<(byte Service, BigInteger Capacity)> services, CancellationToken ct)
    {
        var node = identity.NodeId;
        var list = services.Select(s => new DevFundService(s.Service, s.Capacity.ToString(CultureInfo.InvariantCulture))).ToList();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return await PostAsync(DevFunding.FundPath,
            new DevFundRequest(node, list, ts, identity.SignDevFunding(DevFunding.FundDigest(chainId, node, list, ts))), ct);
    }

    public async Task<FundingResponse> VerifyAsync(byte service, CancellationToken ct)
    {
        var node = identity.NodeId;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return await PostAsync(DevFunding.VerifyPath,
            new DevVerifyRequest(node, service, ts, identity.SignDevFunding(DevFunding.VerifyDigest(chainId, node, service, ts))), ct);
    }

    private async Task<FundingResponse> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(new Uri(gateway, path), body, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => FundingResponse.Ok,
            HttpStatusCode.NotFound => FundingResponse.Unavailable,
            HttpStatusCode.Conflict => FundingResponse.Conflict,
            _ => FundingResponse.Retry,
        };
    }
}
