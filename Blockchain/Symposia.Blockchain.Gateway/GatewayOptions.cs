namespace Symposia.Blockchain.Gateway;

/// <summary>
/// Bootstrap Chain Gateway configuration (issue #110). Points at the L3 RPC
/// endpoint and the deployed NodeRegistry/EpochRootRegistry contracts, and
/// holds the foundation-operated relayer key that sponsors gas for node
/// registration and epoch-root submission (see the Arch pass on #110,
/// "Gas bootstrapping").
/// </summary>
public sealed class GatewayOptions
{
    public string RpcUrl { get; set; } = "http://localhost:8545";
    public string NodeRegistryAddress { get; set; } = "";
    public string EpochRootRegistryAddress { get; set; } = "";

    /// <summary>
    /// Private key of the foundation-operated relayer account that pays gas
    /// for relayed transactions. The relayed node's identity comes from the
    /// EIP-712 signature it supplies, not from this account.
    /// </summary>
    public string RelayerPrivateKey { get; set; } = "";
}
